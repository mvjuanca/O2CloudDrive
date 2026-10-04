# Validación contra O2 real

Fecha: 04/10/2026 (CEST).

## Actualización: test3b, 04/10/2026 21:47 CEST

El usuario completó el login humano en el runner aislado `0.8.4-juanca-test3b`. El ejecutor hizo solo lecturas API y no montó R: ni ejecutó operaciones de archivos.

Resultado observado:

- La petición del validador `GET /sapi/profile/role` recibió HTTP 200, HTTP/1.1, `Content-Type: text/javascript`, sin redirección. El validador la aceptó.
- La sesión capturada informó de `validationkey` presente, 2 cookies, cabecera Cookie presente y User-Agent presente. El mismo objeto `O2Session` y el mismo `HttpClient` se reutilizaron para el cliente API.
- `KeepSessionAlive()` repitió el GET a `/sapi/profile/role` y recibió **401 Unauthorized**, HTML de 503 bytes, sin redirección.
- `GetStorageInfo()` recibió **401 Unauthorized**, HTML de 503 bytes, sin redirección. La excepción sanitizada fue `System.Net.Http.HttpRequestException: Response status code does not indicate success: 401 (Unauthorized).`; no hubo excepción interna de transporte.
- `GetRootFolder()` recibió **401 Unauthorized**, HTML de 503 bytes, sin redirección. `ListRoot` quedó `NOT_RUN` porque no se obtuvo la raíz.
- No se hicieron escrituras remotas, no se montó la unidad y el perfil WebView temporal se eliminó después de completar las lecturas.

La traza sí diferencia una respuesta de autenticación del servidor de DNS/TLS/conexión/timeout: las solicitudes alcanzaron `cloud.o2online.es` y recibieron respuestas HTTP. El handler registró HTTP/1.1, cookie manual presente, `validationkey` presente y User-Agent coincidente en las llamadas API. El `CookieContainer` no contenía cookies para el host; la sesión las envió mediante cabecera manual.

El GET del validador y `KeepSessionAlive()` comparten método, ruta y nombres de parámetros (`action`, `validationkey`), pero la llamada API añade `Origin`, `Referer`, `X-deviceid` y `Accept: application/json`; no se registraron valores secretos ni se hizo una prueba diferencial. La respuesta 200 del validador era `text/javascript`, no JSON autenticado. `O2SessionValidator.IsAuthenticatedResponse` acepta respuestas 2xx que no contengan indicadores conocidos de rechazo, por lo que ese PASS **no demuestra por sí solo** que O2 haya autorizado la API de archivos. La causa de que el servidor devuelva 401 no puede aislarse con esta ejecución: podría ser una sesión/clave no válida para SAPI o una diferencia de contrato/cabeceras; no se atribuye a una de ellas sin evidencia.

Registro sanitizado: `artifacts/real-validation-runner-test3b/runs/20261004-214509-c7145d0d0a80412b9772174f3a9b5661/real-validation.log` (16.045 bytes; SHA-256 `7AAC7B37CE82453AE38B85EEA87B15923C4836A3B20AAC31D35BD9A23DC447DF`). Runner: `artifacts/real-validation-runner-test3b/RealValidation.exe` (151.552 bytes; SHA-256 `5199FC8C43F2514B58904DAE37E3CBB2BEF600069E7E12233BEDE6A0E5054001`).

## Estado

La build `0.8.4-juanca-test2` abrió el login oficial en un perfil WebView2 temporal aislado, sin Credential Manager ni perfil antiguo. El runner eliminó el perfil al cerrar la ventana y no persistió cookies o sesión en disco. La ejecución posterior `test3b` conserva el perfil solo en memoria durante las lecturas y también lo elimina al terminar.

En `test2`, el primer GET de solo lectura de espacio falló con una excepción aún no clasificada. En `test3b`, la traza clasificó el resultado: el servidor respondió 401 Unauthorized a `KeepSessionAlive`, `GetStorageInfo` y `GetRootFolder`; la enumeración de la raíz no llegó a ejecutarse. El runner no montó R:, no creó la carpeta de prueba y no hizo ninguna mutación remota.

La ejecución anterior `test2` no guardaba metadatos HTTP suficientes y por eso dejó el error sin clasificar. `test3b` agregó una traza sanitizada que sí conserva método, ruta saneada, estado, tipo/tamaño de contenido y presencia de campos, sin valores secretos ni cuerpo. El handler aún no tenía asociada la sesión de referencia mientras el validador hacía su request; por eso los indicadores `session_cookie_count=0` y `user_agent_matches_session=false` de esa línea significan “referencia no disponible en el trazador”, no que la petición careciera de Cookie/UA ni que el UA no coincidiera. La petición sí registró `cookie_header_present=true` y `user_agent_present=true`.

## Artefactos

- Aplicación publicada: `artifacts/0.8.4-juanca-test2/`
- Runner aislado: `artifacts/real-validation-runner-test2/RealValidation.exe`
- Registro sanitizado: `artifacts/real-validation-runner-test2/runs/20261004-210323-acc86f3751ee4d4991d094b331f1c72f/real-validation.log`
- Versión de producto: `0.8.4-juanca-test2+9df5380f6307791f22b9e2811372265c4a1ee253`
- App `O2CloudDrive.exe`: 177.664 bytes, SHA-256 `0FB590C7467E5BF9292584534BEA39B8F11F3ABC0D6EF15596599FB583785629`
- App `O2CloudDrive.dll`: 368.640 bytes, SHA-256 `F6318548122ED6BBEE5BCF41FA9F63E73CF52AFF11F48F93F54411F139686C9E`
- Runner `RealValidation.exe`: SHA-256 `52E93924A949ECF367E2BDFF41E3AB6E7BE4F863325DEE6EAB9FF7FF0F92C3F0`

`test1` permanece intacta; sus hashes conocidos siguen coincidiendo con `FINAL_REPORT.md`. No se generó ni sustituyó un ZIP de test1.

## Resultado y siguiente límite

- **Login web:** se completó y el validador devolvió PASS, pero la respuesta 200 `text/javascript` junto con la aceptación permisiva del validador no demuestran una sesión autorizada para la API.
- **KeepSessionAlive, espacio y raíz:** fallan con respuestas HTTP 401 Unauthorized del servidor; raíz no disponible.
- **Listado de raíz:** no ejecutado porque falló `GetRootFolder`.
- **Sobrescritura real V1→V2→V3:** no ejecutada.
- **WinFsp/R:** no montado.
- **Ámbito remoto de prueba:** `O2CloudDrive-Overwrite-Test-20261004` no fue creado ni modificado por esta ejecución.

La causa demostrada del bloqueo es la respuesta HTTP 401 del servidor, no un fallo de transporte. No se ha identificado por qué O2 rechaza estas solicitudes. Antes de cualquier escritura real hace falta una sesión API realmente autorizada y que pasen KeepSessionAlive, espacio, raíz y listado; no volver a intentar escrituras hasta entonces. No instalar como productiva ni configurar inicio automático.
