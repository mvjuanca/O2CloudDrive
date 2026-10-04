# Borrador de seguimiento — NO PUBLICADO

Para https://github.com/chemyglez/O2CloudDrive/issues/1.

He investigado la sobrescritura sobre el HEAD 5369cd4 y preparado una rama local de prueba. El store actual usa UploadFile sin ID de destino, devuelve antes de completar la subida y silencia tanto el fallo de upload como el fallo de MoveToTrash del objeto anterior. Una API falsa con colisión de nombres reproduce original + `(1)` y éxito silencioso. Esto no demuestra qué respuesta HTTP falló en la sesión real del issue.

La corrección experimental sube a nombre temporal único, confirma ID/nombre/tamaño y SHA-256 remoto, conserva el original hasta verificar los bytes nuevos, intercambia nombres y solo entonces mueve el anterior a papelera. Incluye rollback conservador, journals sin secretos, serialización por nodo y error en Flush. Se detectó y corrigió además la omisión de Cleanup al truncar a cero sin Write.

Resultados offline: 28/28 backend/callback/contrato HTTP, 25/25 core net8.0, 11/11 con WinFsp y API en memoria. Falta validación con login humano contra O2 real; no afirmo que el bug esté resuelto en producción.

¿Existe una API soportada para actualizar contenido por ID? Sería preferible a la transacción de aplicación. Quedan costes de doble descarga, consistencia eventual, límites de Cleanup de Windows, escritores externos y recuperación tras crash por revisar antes de integrar.

Se preservó MIT/copyright. No hay PR ni fork publicado; patch y commits permanecen locales.
