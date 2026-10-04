# Corrección implementada

La sustitución vive en `VirtualFileSystem/RemoteFileReplacement.cs`, sin WinFsp, WinForms ni APIs Windows. No se altera el endpoint upload ni se inventa una opción overwrite.

## Transacción de aplicación

1. Serializar por nodo y comprobar el ID/nombre/size/fingerprint original. Negarse ante cambios externos.
2. Hash local y journal durable antes de las mutaciones. Subir `.o2cd-<GUID>.upload<extensión>`.
3. Verificar listado **por ID, nombre y size**, y SHA-256 descargando bloques de hasta 1 MiB. No confiar solo en nombre/tamaño.
4. Volver a comprobar el original; renombrarlo a `.o2cd-<GUID>.backup<extensión>` y confirmar.
5. Promover el nuevo al nombre final; confirmar ID/nombre/size y SHA-256 otra vez.
6. Mover el anterior a papelera y confirmar que dejó la carpeta activa. No borrar permanentemente.
7. Journal completed; actualizar metadata; limpiar dirty y la copia local.

Un archivo nuevo sigue el mismo flujo temporal/verificado, sin retirar un original inexistente. Esto evita declarar nueva creación correcta antes de su confirmación.

## Fallos y recuperación

No se reintenta ciegamente una mutación HTTP cuya respuesta se perdió. Se listan los IDs reales: si el nuevo llegó al nombre final, se devuelve a su temporal; si el antiguo está en backup, se restaura su nombre. Se conserva el temporal en caso de error, además de bytes dirty locales.

Si la retirada se ejecutó pero se perdió su respuesta, el nuevo confirmado permanece válido y el anterior puede estar en papelera. Se notifica error y se bloquea el replay automático: no se restaura/borran objetos por suposición. Igual si falla rollback o no puede verificarse. El journal queda recovery-required, sin limpieza automática de remanentes. Una nueva instancia también se niega a reemplazar ese parent/name si encuentra una transacción incompleta. **No hay autorreparador de transacciones tras crash**; se preserva evidencia y se requiere revisión de IDs/nombres.

El journal contiene fase/hora, padre, nombre, IDs viejo/nuevo, nombres temporal/backup, bytes, SHA-256 y ruta local de retry. No serializa DTOs, tokens, cookies, URLs de descarga ni headers. Se escribe `.next`, Flush(true), y reemplaza el journal; no se promete durabilidad de directorio ante toda pérdida de energía/FS remoto. Los errores API expuestos por la nueva ruta solo conservan el tipo de excepción, no su cuerpo potencialmente secreto.

## Concurrencia / caché / adaptador

- Commit y Write/Replace/SetSize/Rename/Delete usan el mismo lock por CloudNode. Dos Commits próximos de un contenido clean no generan dos uploads.
- El upload no sostiene `_gate`; otra lectura de un nodo no queda retenida por toda la subida. Los locks globales de operaciones preexistentes no se han rediseñado en conjunto.
- RemoteItem conserva metadata antigua; metadata nueva solo tras confirmación. Rename y rollback actualizan su timestamp/fingerprint de servidor para que el retry no confunda un cambio legítimo de metadata con una modificación externa. ReadBlocks usa identidad nueva, sin purge general.
- Flush explícito persiste y puede fallar. Cleanup del último writer persiste, pero Windows no devuelve fallo de Cleanup a la aplicación. Se preserva dirty y el error se registra; no se afirma que Close siempre informe el fallo al usuario.
- `PostCleanupWhenModifiedOnly=false` cubre el truncado sin Write; cerrar lectores no dispara upload de otro writer.
- `O2CLOUD_TRACE_WRITES=1` activa JSON técnico a stderr con fase/nombre/IDs/size. Journal siempre activo; no registra secretos.

## Riesgos y límites pendientes

No es una transacción atómica del servidor: otros clientes pueden ver el temporal/backup o una breve ausencia del nombre final. Hay comprobaciones de conflictos, pero no CAS/version-lock distribuido entre varios PCs. Los temporales fallidos y los objetos en papelera requieren revisión/retención y pueden consumir cuota. La verificación descarga el contenido dos veces: coste alto para archivos grandes. No se probaron cargas de GiB, eventual consistency ni respuestas de O2 real. Una confirmación tardía puede producir fallo conservador en lugar de éxito falso.

El rename independiente con `replaceIfExists` sigue utilizando su algoritmo antiguo (retira destino antes de rename); no está cubierto por una promesa nueva de atomicidad. Se evitó un deadlock al adquirir su segundo nodo con TryEnter, pero no se reescribió toda esa operación. No usar esta build todavía como filesystem de producción: validar primero los archivos de prueba pequeños.
