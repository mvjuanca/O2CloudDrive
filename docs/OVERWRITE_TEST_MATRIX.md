# Matriz de pruebas

Fecha: 04/10/2026. Fake = API de colisiones en memoria + store real. Simulado montado = mismo backend detrás del WinFsp instalado, R: temporal, desmontada al finalizar. Ninguno usa cuenta O2.

| Prueba | Fake | Simulado montado | O2 real |
| --- | --- | --- | --- |
| A Crear nuevo | PASS | PASS | BLOCKED login |
| B Sobrescribir mismo path | PASS | PASS | BLOCKED login |
| C Tres sobrescrituras | PASS | PASS | BLOCKED login |
| D Pequeño→grande | PASS | PASS | BLOCKED login |
| E Grande→pequeño | PASS | PASS | BLOCKED login |
| F Truncar a cero | PASS | PASS tras corregir Cleanup | BLOCKED login |
| G Rename después | PASS | PASS | BLOCKED login |
| H Overwrite tras rename | PASS | PASS | BLOCKED login |
| I Cleanup/Flush múltiples | PASS | PASS Flush repetido | BLOCKED login |
| J Cierre/reapertura | PASS (store nuevo) | PASS | BLOCKED login |
| K Commits próximos | PASS (paralelos) | BLOCKED: no caso montado independiente | BLOCKED login |
| L Error upload | PASS | PASS error Flush | BLOCKED: no inyección real |
| M Error retirada anterior | PASS rollback/retry | BLOCKED: solo backend | BLOCKED: no inyección real |
| N Error rename | PASS rollback/retry | BLOCKED: solo backend | BLOCKED: no inyección real |
| O Error confirmación | PASS dirty/retry | BLOCKED: solo backend | BLOCKED: no inyección real |
| P Caché obsoleta | PASS, bloques 700 kB | BLOCKED: sin prueba de bloques independiente | BLOCKED login |
| Q ID remoto cambiado | PASS | BLOCKED: sin caso separado de IDs | BLOCKED login |
| R Otra lectura durante upload | PASS | BLOCKED: solo backend | BLOCKED login |
| S Carrera Write/Commit | PASS | BLOCKED: solo backend | BLOCKED login |
| T Bytes upload corruptos | PASS preserva original | BLOCKED | BLOCKED |
| U Respuesta perdida tras backup | PASS rollback | BLOCKED | BLOCKED |
| V Rollback fallido + instancia nueva | PASS bloquea replay | BLOCKED | BLOCKED |
| W Respuesta perdida tras papelera | PASS nuevo válido, error/recovery | BLOCKED | BLOCKED |
| X Fallo explícito Flush | PASS callback | PASS NTSTATUS→IOException | BLOCKED login |
| Y Cierre lector con writer activo | PASS callback | BLOCKED: sin caso montado separado | BLOCKED login |
| Z Dos handles writer | PASS callback | BLOCKED: sin caso montado separado | BLOCKED login |
| AA Petición multipart real | PASS transporte interceptado | No aplicable | No se envió |
| AB Versión remota cambiada por otro cliente | PASS rechaza conflicto | BLOCKED | BLOCKED |

Totales: Windows backend/callback/HTTP **28/28**, core `net8.0` **25/25**, montaje Windows **11/11**, self-test InMemory **PASS**. No sumar columnas como tests independientes de producción. Los casos I/X core ejercitan Commit, no callbacks WinFsp.

Registros: `artifacts/fix-tests.log`, `core-net8-tests.log`, `mounted-tests.log`, `fixed-self-test.log`.
Antes: `baseline-reproduction.log` demuestra el defecto y `baseline-regression.log` recoge 2/19 PASS. El primer test montado corrigió una omisión real del truncado a cero; `mounted-trace.log` conserva la evidencia previa.

Repetir offline: `tools/Build-Test.ps1`. Requiere SDK .NET 8 y WinFsp para el bloque montado; los tests core no necesitan WinFsp.

## Validación real: lectura iniciada, overwrite no ejecutado

Runner actual: `artifacts/real-validation-runner-test2/RealValidation.exe --login-only`. Usa login O2 humano nuevo en perfil WebView temporal, sin Credential Manager ni sesión antigua y sin límite de 90 s. Después de que el validador aceptó una sesión en memoria, intentó solo lecturas: espacio, raíz y listado. El primer GET de espacio falló con `HttpRequestException`, así que la raíz no llegó a consultarse. Perfil temporal eliminado; no montó R: ni escribió en O2.

El runner `--validate` sigue siendo el que puede ejecutar la prueba controlada, solo después de superar lecturas de espacio y raíz. Su guard limita operaciones al ID de `O2CloudDrive-Overwrite-Test-20261004`; se niega a pisar archivos de control previos. No hay Delete permanente ni Restore habilitado. La única mutación preparatoria prevista es crear la carpeta de control en raíz, expresamente dentro del alcance autorizado. Los errores artificiales no se inyectan en la cuenta real.

La matriz de resultados reales permanece **BLOCKED/no validada** para todos los casos de overwrite, rename, tamaño, remount y conflicto. No confundir los PASS de backend simulado con validación de producción. Detalles del login y lectura fallida: `LOGIN_FLOW_DIAGNOSIS.md` y `REAL_O2_VALIDATION.md`.
