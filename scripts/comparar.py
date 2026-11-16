"""Compara dos huellas (origen vs. restaurado) y escribe una tabla Markdown.

Uso: python comparar.py origen.json restaurado.json   -> exit 1 si algo difiere
"""
import json
import sys

origen = json.load(open(sys.argv[1], encoding="utf-8"))
restaurado = json.load(open(sys.argv[2], encoding="utf-8"))

ok = True
print("| Colección | Origen | Restaurado | SHA-256 origen | SHA-256 restaurado | Resultado |")
print("|---|---:|---:|---|---|---|")
for nombre, o in origen.items():
    r = restaurado.get(nombre, {"documentos": 0, "sha256": "-"})
    igual = o == r
    ok &= igual
    print(f"| `{nombre}` | {o['documentos']} | {r['documentos']} | `{o['sha256'][:16]}…` "
          f"| `{r['sha256'][:16]}…` | {'✅ idéntica' if igual else '❌ difiere'} |")
sys.exit(0 if ok else 1)
