# Baseline — 04/10/2026

Workspace: `C:\Users\mvjua\Documents\Proyectos\O2CloudDrive-overwrite-fix`.
Upstream público: https://github.com/chemyglez/O2CloudDrive.
HEAD observado y clonado: `5369cd42d9e8e43871a41297262e7b998db4b1e0`.
Rama local: `fix/overwrite-existing-files`. El remoto del clone se renombró de `origin` a `upstream`; no existe fork ni se hizo push.

## Entorno realmente comprobado

| Elemento | Resultado |
| --- | --- |
| Windows | NT 10.0.26200, x64 |
| Git | 2.56.0.windows.1 |
| SDK .NET inicial | No disponible en PATH ni en las ubicaciones estándar revisadas |
| SDK utilizado | 8.0.425 portable, `.dotnet/sdk`, runtime 8.0.31; sin modificación de PATH global |
| Build Tools | Entrada de VS 2022 Build Tools 17.14.39; su ruta E: registrada ya no existe. No se necesitó MSVC para este build |
| WinFsp | WinFsp 2025, 2.1.25156; binarios presentes, launcher ejecutándose |
| WebView2 | 154.0.4258.53 presente |
| O: | No montada |
| O2CloudDrive | Sin proceso activo |

Hay contradicción con la desinstalación comunicada: siguen existiendo `C:\Program Files\O2 Cloud Drive`, su entrada de desinstalación 0.8.4 beta y los directorios `Cache`, `Logs`, `WebView2`, `WriteCache` bajo `%LOCALAPPDATA%\O2CloudDrive`. Se inventariaron nombres/metadatos; no se leyó configuración privada, Credential Manager, cookies ni validationkeys. No se borró ni reinstaló el programa antiguo.

SDK oficial: https://builds.dotnet.microsoft.com/dotnet/Sdk/8.0.425/dotnet-sdk-8.0.425-win-x64.zip.
SHA-512 verificado antes de extraer:
`f0b6f15bf6f1a0507205c0cb102ab99e1dee875c4682c8ed94665be1d580186a06b21455e83b3a01a0ff7f4cd887b67420f2e2fe09ed985534a4cea488ae1af9`.

## Pruebas anteriores a la corrección

- `dotnet build src/O2CloudDrive/O2CloudDrive.csproj -c Release`: PASS, 0 warnings / 0 errores, 7,19 s según MSBuild. Registro `artifacts/baseline-build.log`.
- `--self-test --cache artifacts/baseline-cache`: PASS; lectura, creación, escritura, rename y borrado del backend InMemory. Registro `artifacts/baseline-self-test.log`. Este self-test no prueba el API ni las colisiones remotas.
- Reproducción `tests/OverwriteTests --reproduce`: PASS como reproducción del defecto. Misma longitud V1/V2, API falsa con sufijo y fallo de papelera: remoto `file-1 control.txt` V1 + `file-2 control (1).txt` V2, dirty=false sin error.
- SHA-256 V1: `568BF3906DBC5CBBEC67AE14F6D8136254723DE0952BE033C3B48A20DD20FE88`.
- SHA-256 V2: `C44F03FF2521B0504A2EEE68630FDA18C0E8727325376DDF16B63277E5ADF7D4`.
- Matriz inicial: 2/19 PASS; 17 fallos de semántica/error. Registro `artifacts/baseline-regression.log`.

Antes de instanciar el store original en tests se añadió únicamente una ruta de WriteCache inyectable. Así no se ejecutó su purga sobre la caché histórica del usuario. La reproducción no alteró el algoritmo de subida. Los datos falsos viven en memoria y en carpetas temporales únicas.

Dependencias NuGet existentes: `Microsoft.Web.WebView2` 1.0.2792.45 y `winfsp.net` 2.1.25156. MIT y copyright permanecen intactos.

El issue https://github.com/chemyglez/O2CloudDrive/issues/1 seguía abierto al consultarlo. No se publicó ninguna respuesta.
