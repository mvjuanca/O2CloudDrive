# Validación contra O2 real

Fecha: 04/10/2026 (CEST).

## Estado

La build `0.8.4-juanca-test2` abrió el login oficial en un perfil WebView2 temporal aislado, sin Credential Manager ni perfil antiguo. El runner eliminó el perfil al cerrar la ventana y no persistió cookies o sesión en disco.

El flujo pasó por el acceso de O2/Telefónica y `O2SessionValidator` aceptó una sesión en memoria. A continuación, el primer GET de solo lectura de espacio (`GetStorageInfo`) falló con `HttpRequestException`. Por orden, la enumeración de la raíz no llegó a ejecutarse. El runner se detuvo; no montó R:, no creó la carpeta de prueba y no hizo ninguna mutación remota.

La instrumentación deliberadamente no guarda teléfono, SMS/PIN, cookies, validation key, URL con query dinámico ni cuerpo de error. Por ello no puede afirmarse quién completó la interacción ni atribuir la excepción a un estado HTTP concreto. No se reintentó la llamada.

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

- **Login web:** la navegación oficial y validación de sesión llegaron a completarse en memoria; no se declara todavía validación funcional de API.
- **Lectura de espacio/raíz:** no validada; espacio falló primero con `HttpRequestException`, raíz no ejecutada.
- **Sobrescritura real V1→V2→V3:** no ejecutada.
- **WinFsp/R:** no montado.
- **Ámbito remoto de prueba:** `O2CloudDrive-Overwrite-Test-20261004` no fue creado ni modificado por esta ejecución.

Antes de cualquier escritura real debe investigarse el fallo de transporte/solicitud de `GetStorageInfo` sin registrar secretos y luego completar las lecturas de espacio y raíz. Solo si ambas funcionan se puede continuar con la prueba ya acotada a la carpeta de control, preservando el guard de IDs y todas las condiciones de parada existentes. No instalar como productiva ni configurar inicio automático.
