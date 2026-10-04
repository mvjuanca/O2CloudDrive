# Causa raíz: hechos y límites

## Demostrado en código y ejecución offline

1. La subida original es `POST https://upload.cloud.o2online.es/sapi/upload?action=save` con multipart `data` y `file`. Metadata: nombre, size, modificationdate, folderid y contenttype cuando procede. No transmite el ID del objeto existente. El test AA intercepta el **cliente real** con HttpMessageHandler, inspecciona esa petición y detiene el transporte sin acceder a red.
2. Commit anterior devuelve antes del upload y marca clean anticipadamente. Puede haber escrituras simultáneas sobre el archivo local que el upload todavía está leyendo.
3. Si el upload retorna un ID distinto, intenta `MoveToTrash(previous)` y silencia cualquier error. El catch exterior también oculta los fallos de upload.
4. La API falsa reproduce una colisión O2-like y el fallo de retirada: V1 y V2 sobreviven como `control.txt` y `control (1).txt`; el store devuelve éxito y dirty=false. Con retirada correcta el nombre original puede desaparecer y quedar solo el sufijo.
5. `UploadFileCore` intenta confirmar por ID; si no lo encuentra, admite búsqueda por nombre/tamaño. Para bytes nuevos de idéntico tamaño puede aceptar el objeto anterior. Es un riesgo de confirmación incorrecta visible en código, no una captura nueva de respuesta real que demuestre que ocurrió en la cuenta.
6. Después se elimina la copia local. Un DTO anterior/incorrecto conduce de nuevo a bytes antiguos; un DTO nuevo pero con nombre remoto diferente puede verse localmente bajo un nombre distinto hasta enumerar otra vez. No hace falta culpar exclusivamente a ReadBlocks para explicar la inconsistencia.

## Evidencia histórica real, no repetida hoy

El issue #1 describe V1 aún visible en O: y un duplicado `(1)` con V2 en la web. Se usa como antecedente, no como un nuevo test de producción. La reproducción confirma que el algoritmo permite ese resultado, pero no identifica la respuesta HTTP o el fallo concreto de papelera de aquella sesión.

## Capacidades API existentes

- Upload crea un objeto; no se ha identificado un update nativo de contenido por ID validado.
- `RenameOrMove`: `upload/{mediaKind}?action=save-metadata`, ID/name/folderid; no sustituye bytes.
- `MoveToTrash`: `media/{mediaKind}?action=delete&softdelete=true`; no se usa borrado permanente en la corrección.
- ListFolder y DownloadFile permiten comprobar metadatos y bytes.

La ausencia de un endpoint en este cliente/historia no prueba que O2 carezca de él. Como contraste documental se revisó https://github.com/dbaratech/o2cloud/blob/main/docs/api-reference.md, un cliente independiente, no una especificación oficial. Menciona revisiones get/restore, no demuestra un upload-update. No se inventaron parámetros ni se realizaron pruebas sobre la cuenta para descubrirlos.

## Clasificación

Causa de diseño demostrada: **crear otro objeto + confirmación insuficiente + éxito anticipado + errores silenciados** no proporciona semántica fiable de overwrite. La regla exacta de sufijos del servidor y la causa HTTP del episodio histórico siguen pendientes de validación real. Versiones con esta ruta: al menos los tags 0.7–0.8.4 examinados.
