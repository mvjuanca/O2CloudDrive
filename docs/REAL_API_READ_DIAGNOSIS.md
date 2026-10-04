# Diagnóstico de lecturas API reales de O2 Cloud

Fecha: 04/10/2026. Alcance: login humano y lecturas GET/POST no mutantes. No se montó el drive ni se ejecutaron create/upload/rename/delete/trash.

## Evidencia de test3b

Registro sanitizado: `artifacts/real-validation-runner-test3b/runs/20261004-214509-c7145d0d0a80412b9772174f3a9b5661/real-validation.log`.

| Paso | Solicitud | Resultado observado |
|---|---|---|
| O2SessionValidator | GET `/sapi/profile/role`, `action` y `validationkey` | HTTP 200, `text/javascript`, sin redirect; validador PASS |
| KeepSessionAlive | GET `/sapi/profile/role`, mismos nombres de query | HTTP 401, `text/html`, 503 bytes, sin redirect; método devuelve FAIL |
| GetStorageInfo | GET `/sapi/media`, `action`, `softdeleted`, `validationkey` | HTTP 401, `text/html`, 503 bytes, sin redirect; `HttpRequestException` por `EnsureSuccessStatusCode` |
| GetRootFolder | POST `/sapi/media/folder/root`, `action`, `validationkey`, sin body | HTTP 401, `text/html`, 503 bytes, sin redirect; `HttpRequestException` |
| ListRoot | No solicitado | NOT_RUN: root unavailable |

La excepción exacta registrada para espacio y raíz fue `System.Net.Http.HttpRequestException: Response status code does not indicate success: 401 (Unauthorized).`; `HttpRequestError=Unknown`, `StatusCode=Unauthorized`. El 401 demuestra que las solicitudes llegaron al servidor y recibieron una respuesta HTTP. No hubo evidencia de DNS, conexión, TLS, timeout ni redirect fallido. El cuerpo HTML no se guardó, así que no conocemos el mensaje explicativo del servidor.

## Sesión, cliente y diferencias

- La sesión capturada indicó `validationkey_present=true`, `cookie_count=2`, `cookie_header_present=true` y `user_agent_present=true`.
- El runner pasó el mismo objeto `O2Session` a `MemoryAuth`/`O2CloudApiClient` y reutilizó el mismo `HttpClient` empleado por el validador.
- Las lecturas API enviaron manualmente Cookie y validationkey; `CookieContainer` estaba habilitado pero vacío para `cloud.o2online.es`. No se afirma que la cookie jar deba estar poblada cuando la cabecera manual está presente.
- El GET del validador y KeepSessionAlive coinciden en método, ruta y nombres de parámetros. En el cliente API, además, se envían Origin, Referer, `X-deviceid` y `Accept: application/json`; el validador no envía esas cabeceras. La traza no registra valores de cookies, validationkey ni cabeceras sensibles.
- GetStorageInfo cambia además de endpoint/query, y GetRootFolder usa POST. Como los tres pasos API devuelven el mismo 401, el fallo no queda limitado al endpoint de espacio.
- Las respuestas de las llamadas API conservaron la ruta original, sin redirección automática observable. Se usó HTTP/1.1. La configuración diagnóstica mantuvo TLS y proxy del sistema sin personalizaciones; redirects automáticos estaban activados, pero no ocurrieron en estas respuestas.

## Causa y límites

**Causa inmediata demostrada:** O2 rechazó las solicitudes API con 401 Unauthorized.

**No demostrado:** el motivo interno de rechazo. No se puede distinguir con estos datos entre una sesión/validationkey que no autoriza SAPI, cookies que no corresponden a esa sesión, una diferencia de contrato de cabeceras (incluidas Origin/Referer/Accept/X-deviceid), o una respuesta genérica de autenticación del backend.

El PASS del validador es débil: su implementación acepta un 2xx no vacío si el texto no contiene marcadores conocidos de rechazo/error. En este caso el tipo era `text/javascript`, no una respuesta JSON autenticada. Por ello test3b no demuestra que la sesión estuviera autorizada para la API; el resultado puede ser un falso positivo del validador. Esta limitación explica por qué no debe interpretarse el PASS como prueba de que `GetStorageInfo` tendría que funcionar, pero no identifica por qué se produjo el 401.

No se inspeccionó ni almacenó el cuerpo HTML de error por privacidad. La traza sanitizada solo conserva método, rutas/query redactadas, presencia de campos, metadatos HTTP y resultados. No se repitieron solicitudes después de observar el 401.

## Estado final

- KeepSessionAlive: FAIL (401).
- GetStorageInfo: FAIL (401).
- GetRootFolder: FAIL (401).
- ListRoot: NOT_RUN.
- Escrituras remotas: ninguna.
- Perfil temporal: eliminado después de las lecturas.
- Overwrite real: no ejecutado.

Conclusión: la API real sigue no operativa para este cliente. No hay base para modificar el overwrite ni añadir un fallback. Una futura investigación deberá primero obtener una sesión/probe que el servidor confirme de forma inequívoca y, si persiste el 401, aislar el contrato de autenticación con evidencia segura; cualquier nuevo intento requerirá una ejecución humana autorizada. No usar el cuerpo ni las cabeceras de error sin redacción de secretos.

## Integridad del registro

- Tamaño: 16.045 bytes.
- SHA-256: `7AAC7B37CE82453AE38B85EEA87B15923C4836A3B20AAC31D35BD9A23DC447DF`.
- Runner: `artifacts/real-validation-runner-test3b/RealValidation.exe`, 151.552 bytes, SHA-256 `5199FC8C43F2514B58904DAE37E3CBB2BEF600069E7E12233BEDE6A0E5054001`.
