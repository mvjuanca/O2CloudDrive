# Portabilidad Linux — análisis, no port completo

1. **Partes compartibles:** DTO/API HTTP/JSON, IO2CloudApiClient, CloudNode, lógica de paths, store, ReadCache/WriteCache, RemoteFileReplacement, transferencias y gran parte de la configuración. No dependen intrínsecamente de Windows.
2. **Windows-only:** O2VirtualFileSystem/FileSystemHost WinFsp; WinForms/WebView2; WindowsCredentialSessionStore y sus CredRead/CredWrite; shell/Explorer/registro, iconos/tray e instaladores Windows.
3. **Reutilización:** medición orientativa del árbol: 5.714 de 11.133 líneas en Api/VirtualFileSystem/Caching/Transfers excluyendo el adaptador WinFsp y su contexto de enumeración, aproximadamente 51%. Es una cuenta de líneas candidatas, **no** porcentaje de port ya hecho ni coste/esfuerzo. También hay interfaces/auth/config reutilizables fuera de esos directorios. `tests/CoreTests` compila el store/reemplazo realmente existente para net8.0 y pasa 25 casos sin paquetes Windows; se ejecutó en Windows, no constituye prueba runtime Linux.
4. **Sustituto WinFsp:** adaptador FUSE que traduzca lookup/open/read/write/truncate/flush/release/rename/unlink/errores hacia ICloudFileStore. No trasladar callbacks NTSTATUS tal cual.
5. **FUSE/FUSE3:** técnicamente viable como filesystem de usuario. Hará falta elegir un binding .NET mantenido o shim nativo libfuse3, controlar cancelación, handles, permisos y errno; **no se seleccionó ni instaló una dependencia**. Evaluación actual pendiente, no promesa de driver listo.
6. **Autenticación Linux:** flujo humano oficial en navegador/perfil aislado, retorno de sesión por un mecanismo legítimo a validar. El mecanismo WebView2 actual no corre en Linux. No asumir que OAuth genérico/device code existe en O2; no extraer sesiones de otro navegador.
7. **Alternativa Credential Manager:** implementación ISessionStore mediante Secret Service/keyring; opción CLI de archivo privado modo 600 como decisión futura explícita, no almacenar secretos en Git ni stdout.
8. **Interfaz gráfica:** no obligatoria para el mount/store. Un CLI puede gestionarlos, pero login humano necesitará navegador y un retorno validado. UI opcional separada de Core.
9. **csproj:** Core `net8.0` sin UseWindowsForms/WinFsp/WebView2/RuntimeIdentifier win-x64; Windows `net8.0-windows` con esas dependencias; Linux net8.0, RID linux-x64/linux-arm64 cuando toque y dependencia FUSE propia. No se ha ejecutado esta migración de producción.
10. **.NET 8:** el código de Core probado usa APIs estándar .NET 8. El soporte/SDK a seleccionar para un port futuro debe revisarse en ese momento; no se puede inferir del test de Windows el despliegue en la Pi.
11. **Fix idéntico:** RemoteFileReplacement, confirmación SHA-256/ID/nombre, journals, política de rollback y retry son los mismos. Cada adaptador debe ofrecer una operación flush con error; FUSE release tampoco sustituye la semántica de flush/fsync. Directorios de caché/journal son inyectables.
12. **Arquitectura:** Core común + Windows/WinFsp + Linux/FUSE, con auth/session/mount/UI detrás de interfaces. La lógica de reemplazo nunca debe duplicarse en el adaptador FUSE.

Respuesta: **sí, el código corregido de overwrite puede reutilizarse en Linux**, sustentado en su ubicación real y en el build/test net8.0. No es una versión Linux lista: faltan adaptador FUSE, login, keyring, rutas/permisos, ciclo de handles, packaging y pruebas reales en ese SO.

No se instaló FUSE, no se accedió a Linux remoto ni se tocó la Raspberry.

Referencias primarias del análisis conceptual: [libfuse](https://github.com/libfuse/libfuse) y [distribuciones Linux soportadas por .NET](https://learn.microsoft.com/en-us/dotnet/core/install/linux). No se copió ni ejecutó código de esos proyectos.
