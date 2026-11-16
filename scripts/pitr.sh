#!/usr/bin/env bash
# Point-in-time recovery (PITR) con mongodump --oplog + replay del oplog.
#
# 1. "Producción": replica set de un nodo con 1000 préstamos.
# 2. Respaldo completo con mongodump --oplog (consistente aunque haya escrituras).
# 3. Después del respaldo llegan 250 préstamos nuevos y 100 devoluciones.
# 4. Desastre: un deleteMany({}) borra toda la colección.
# 5. Se respalda el oplog (local.oplog.rs) desde el respaldo completo en adelante.
# 6. Se busca en el oplog la primera operación del desastre: ese es el límite.
# 7. En un servidor nuevo: restaurar el completo (pierde lo del paso 3) y luego
#    reproducir el oplog hasta un instante antes del desastre (recupera todo).
# 8. Se compara la huella SHA-256 con la tomada justo antes del desastre.
#
# Requiere: docker y python con pymongo. Variables opcionales: PY, WORK.
# shellcheck disable=SC2016  # el $ de los comandos de mongosh es JavaScript, no shell
set -euo pipefail
export MSYS_NO_PATHCONV=1

IMG=mongo:8.0
NET=pitr-net
PY=${PY:-python3}
DIR=$(cd "$(dirname "$0")" && (pwd -W 2>/dev/null || pwd))
WORK=${WORK:-$PWD/pitr-trabajo}
rm -rf "$WORK" && mkdir -p "$WORK" && chmod 777 "$WORK"   # los contenedores de herramientas escriben aquí
WORK_DOCKER=$(cd "$WORK" && (pwd -W 2>/dev/null || pwd))
PROD="mongodb://localhost:27101/?directConnection=true"
DR="mongodb://localhost:27102/?directConnection=true"

msh()   { docker exec "$1" mongosh --quiet --eval "$2"; }
tools() { docker run --rm --network "$NET" -v "$WORK_DOCKER:/w" "$IMG" "$@"; }
huella(){ "$PY" "$DIR/huella.py" "$1" biblioteca prestamos | "$PY" -c 'import sys,json; d=json.load(sys.stdin)["prestamos"]; print(d["documentos"], d["sha256"])'; }
ultimo_ts() { msh pitr-prod 'const e=db.getSiblingDB("local").oplog.rs.find().sort({$natural:-1}).limit(1).next(); print(e.ts.getHighBits()+" "+e.ts.getLowBits())'; }
ahora_ms() { date +%s%3N; }

limpiar() { docker rm -f pitr-prod pitr-dr >/dev/null 2>&1 || true; docker network rm "$NET" >/dev/null 2>&1 || true; }
trap limpiar EXIT
limpiar
docker network create "$NET" >/dev/null

echo "==> Levantando 'producción' (rs0) y el servidor de recuperación (rs1)"
docker run -d --name pitr-prod --network "$NET" -p 27101:27017 "$IMG" --replSet rs0 --bind_ip_all >/dev/null
docker run -d --name pitr-dr   --network "$NET" -p 27102:27017 "$IMG" --replSet rs1 --bind_ip_all >/dev/null
for c in pitr-prod pitr-dr; do
  for _ in $(seq 1 60); do msh "$c" 'db.runCommand({ping:1}).ok' >/dev/null 2>&1 && break; sleep 1; done
done
msh pitr-prod 'rs.initiate({_id:"rs0",members:[{_id:0,host:"pitr-prod:27017"}]})' >/dev/null
msh pitr-dr   'rs.initiate({_id:"rs1",members:[{_id:0,host:"pitr-dr:27017"}]})' >/dev/null
for c in pitr-prod pitr-dr; do
  for _ in $(seq 1 60); do [ "$(msh "$c" 'db.hello().isWritablePrimary' 2>/dev/null)" = "true" ] && break; sleep 1; done
done

echo "==> 1. Cargando 1000 préstamos"
msh pitr-prod '
  const d = db.getSiblingDB("biblioteca"); const base = new Date("2026-11-01T08:00:00Z");
  const docs = [];
  for (let i = 1; i <= 1000; i++) docs.push({ _id: "PRE-" + String(i).padStart(5, "0"), socioId: "SOC-" + String(1 + i % 40).padStart(3, "0"),
    libroId: "LIB-" + String(1 + i % 30).padStart(3, "0"), fechaPrestamo: new Date(base.getTime() + i * 60000), estado: "activo" });
  d.prestamos.insertMany(docs); d.prestamos.createIndex({ socioId: 1, fechaPrestamo: -1 });' >/dev/null

echo "==> 2. Respaldo completo con mongodump --oplog"
read -r T_BACKUP I_BACKUP < <(ultimo_ts)
t0=$(ahora_ms)
tools mongodump --uri "mongodb://pitr-prod:27017/?directConnection=true" --oplog --gzip --archive=/w/completo.archive.gz 2>&1 | tail -1
MS_DUMP=$(( $(ahora_ms) - t0 ))

echo "==> 3. Escrituras posteriores al respaldo (250 préstamos nuevos, 100 devoluciones)"
msh pitr-prod '
  const d = db.getSiblingDB("biblioteca");
  for (let i = 1001; i <= 1250; i++) d.prestamos.insertOne({ _id: "PRE-" + String(i).padStart(5, "0"), socioId: "SOC-" + String(1 + i % 40).padStart(3, "0"),
    libroId: "LIB-" + String(1 + i % 30).padStart(3, "0"), fechaPrestamo: new Date(), estado: "activo" });
  d.prestamos.updateMany({ _id: { $lte: "PRE-00100" } }, { $set: { estado: "devuelto", fechaDevolucion: new Date() } });' >/dev/null
read -r N_OK SHA_OK < <(huella "$PROD")
echo "    estado correcto justo antes del desastre: $N_OK documentos, sha256 ${SHA_OK:0:16}…"

echo "==> 4. DESASTRE: deleteMany({}) en biblioteca.prestamos"
read -r T_OK I_OK < <(ultimo_ts)
sleep 1
msh pitr-prod 'db.getSiblingDB("biblioteca").prestamos.deleteMany({})' >/dev/null
read -r N_MAL _ < <(huella "$PROD")
echo "    después del desastre: $N_MAL documentos"

echo "==> 5. Respaldo del oplog desde el respaldo completo"
tools mongodump --uri "mongodb://pitr-prod:27017/?directConnection=true" -d local -c oplog.rs \
  --query "{\"ts\":{\"\$gt\":{\"\$timestamp\":{\"t\":$T_BACKUP,\"i\":$I_BACKUP}}}}" --out /w/oplog 2>&1 | tail -1
mkdir -p "$WORK/replay" && cp "$WORK/oplog/local/oplog.rs.bson" "$WORK/replay/oplog.bson"
ENTRADAS=$(msh pitr-prod "db.getSiblingDB('local').oplog.rs.countDocuments({ts:{\$gt:Timestamp({t:$T_BACKUP,i:$I_BACKUP})}})")

echo "==> 6. Buscando en el oplog la primera operación del desastre"
read -r T_D I_D OP_D < <(msh pitr-prod "
  const e = db.getSiblingDB('local').oplog.rs.find({ ts: { \$gt: Timestamp({ t: $T_OK, i: $I_OK }) }, op: { \$ne: 'n' } }).sort({ \$natural: 1 }).limit(1).next();
  print(e.ts.getHighBits() + ' ' + e.ts.getLowBits() + ' ' + e.op + ':' + (e.ns || '') + (e.o && e.o.applyOps ? '(applyOps ' + e.o.applyOps.length + ' ops)' : ''))")
echo "    primera operación del desastre: $OP_D en $T_D:$I_D  →  --oplogLimit $T_D:$I_D"

echo "==> 7a. Restaurar SOLO el respaldo completo en el servidor nuevo"
t0=$(ahora_ms)
tools mongorestore --uri "mongodb://pitr-dr:27017/?directConnection=true" --gzip --archive=/w/completo.archive.gz --oplogReplay --drop 2>&1 | tail -1
MS_COMPLETO=$(( $(ahora_ms) - t0 ))
read -r N_COMPLETO SHA_COMPLETO < <(huella "$DR")
echo "    con solo el completo: $N_COMPLETO documentos (se perdieron $((N_OK - N_COMPLETO)) préstamos y las devoluciones)"

echo "==> 7b. Reproducir el oplog hasta un instante antes del desastre"
t0=$(ahora_ms)
tools mongorestore --uri "mongodb://pitr-dr:27017/?directConnection=true" --oplogReplay --oplogLimit "$T_D:$I_D" /w/replay 2>&1 | tail -1
MS_REPLAY=$(( $(ahora_ms) - t0 ))
read -r N_PITR SHA_PITR < <(huella "$DR")

echo "==> 8. Verificación"
VERIFICADO=false
[ "$N_PITR" = "$N_OK" ] && [ "$SHA_PITR" = "$SHA_OK" ] && VERIFICADO=true
echo "    PITR: $N_PITR documentos, sha256 ${SHA_PITR:0:16}…  →  verificado=$VERIFICADO"

cat > "$WORK/resultado.json" <<EOF
{
  "documentosAntesDelDesastre": $N_OK,
  "documentosTrasElDesastre": $N_MAL,
  "documentosSoloCompleto": $N_COMPLETO,
  "documentosPitr": $N_PITR,
  "sha256Correcto": "$SHA_OK",
  "sha256Completo": "$SHA_COMPLETO",
  "sha256Pitr": "$SHA_PITR",
  "oplogEntradas": $ENTRADAS,
  "oplogLimit": "$T_D:$I_D",
  "operacionDesastre": "$OP_D",
  "msDump": $MS_DUMP,
  "msRestauracionCompleto": $MS_COMPLETO,
  "msReplayOplog": $MS_REPLAY,
  "verificado": $VERIFICADO
}
EOF
cat "$WORK/resultado.json"
[ "$VERIFICADO" = true ]
