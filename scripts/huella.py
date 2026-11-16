"""Huella de una base MongoDB: conteo + SHA-256 de los documentos BSON ordenados por _id.

Calcula exactamente lo mismo que la app (/api/estado), así se puede comparar
producción, el respaldo restaurado y lo que ve la app.

Uso: python huella.py <uri> <base> [coleccion ...] > huella.json
"""
import hashlib
import json
import sys

from bson.raw_bson import RawBSONDocument
from pymongo import MongoClient

COLECCIONES = ["libros", "socios", "prestamos"]

uri, base = sys.argv[1], sys.argv[2]
colecciones = sys.argv[3:] or COLECCIONES
cliente = MongoClient(uri, document_class=RawBSONDocument, serverSelectionTimeoutMS=20000)
resultado = {}
for nombre in colecciones:
    sha, n = hashlib.sha256(), 0
    for doc in cliente[base][nombre].find({}, sort=[("_id", 1)], batch_size=1000):
        sha.update(doc.raw)
        n += 1
    resultado[nombre] = {"documentos": n, "sha256": sha.hexdigest()}
print(json.dumps(resultado, indent=2))
