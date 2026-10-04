# Arquitectura futura propuesta

```text
O2CloudDrive.Core (net8.0)
  Api / DTO / IO2CloudApiClient
  VirtualFileSystem lógico / ICloudFileStore / CloudNode
  RemoteFileReplacement / journals / rollback
  caché / transferencias / interfaces de sesión
       ↑                         ↑
O2CloudDrive.Windows       O2CloudDrive.Linux
  WinFsp                    FUSE3 / errno / handles
  WinForms / WebView2        CLI + login humano validado
  Credential Manager        Secret Service / keyring
  Explorer / installer      packaging / rutas XDG
```

Es una propuesta, no tres aplicaciones creadas. La única separación de producción realizada es extraer la interfaz IO2CloudApiClient a su archivo propio (sin cambiar sus métodos) para poder compilar tests Core sin arrastrar UI/WinFsp. `tests/CoreTests` enlaza los mismos archivos de producción; no mantiene una copia divergente del algoritmo.

Fronteras a decidir: sesión en memoria + session store por plataforma; servicios de reloj/journal/cache cuando haga falta; adaptador de errores; coordinación/cancelación de operaciones largas; derechos/permisos locales. Los DTOs que contienen tokens solo viven en memoria y no son el formato del journal. El adapter no debe conocer un endpoint de replace distinto al usado por Core.

No se anticipó una refactorización grande, no se convirtió el árbol productivo en un nuevo repositorio y no se cambiaron componentes de RadioAdmin.
