# Plan PR — NO CREADA

Rama local `fix/overwrite-existing-files`; remoto `upstream` oficial. Sin push.

Cambios revisables:

1. Test que reproduce colisión + retirada fallida; ruta de caché inyectable para no tocar datos del usuario.
2. Reemplazo remoto compartido, journal y rollback; RemoteItem; serialización por nodo; Flush; Cleanup de cero bytes/último writer.
3. Regresión de errores/carreras/hash/IDs/caché y prueba montada; compilación Core net8.0 sin Windows.
4. Runner de validación humana aislada, versión test, informes y script reproducible.

Antes de proponer merge:

- Completar V1→V2→V3 real con IDs/hashes y remount en carpeta aislada.
- Confirmar comportamiento de metadata rename/papelera/consistencia eventual con O2 y valorar API update nativa.
- Revisar coste/timeout de la doble descarga y política de retención de journals/temporales; no purgar retry data automáticamente.
- Revisar limitación Windows Cleanup y rename replaceIfExists fuera del fix; no vender atomicidad del servidor.
- Adaptar identificación de build según maintainer (la presente no es release oficial).
- Decidir separar runner/docs extensos de la PR mínima, manteniendo tests esenciales.

Estado: **preparado para revisión técnica, no listo para merge/uso general** sin validación O2 real. Publicar el borrador, fork o PR requiere autorización posterior. No escribir nada automáticamente en GitHub.
