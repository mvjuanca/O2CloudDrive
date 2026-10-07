# Inicio de sesión, validación y montaje automático

## Inicio de la aplicación

En Windows, el mecanismo validado para iniciar O2CloudDrive en la sesión del
usuario es la tarea programada `O2CloudDrive - Start at logon`. Su configuración
es:

- desencadenador al iniciar sesión del usuario actual, con retraso de 15 s;
- acción `C:\Program Files\O2 Cloud Drive\O2CloudDrive.exe`;
- sesión interactiva, nivel de privilegios limitado y ninguna contraseña
  almacenada;
- `MultipleInstances = IgnoreNew`, sin detener la tarea por batería.

La antigua entrada `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\O2CloudDrive`
se retiró antes de la prueba final; la tarea quedó como único mecanismo
configurado. No se incluye aquí un script temporal de creación de la tarea.

La aplicación también protege su propio inicio con un mutex de instancia única.
La tarea no requiere elevación administrativa.

## Validación de sesión y auto-montaje

Al arrancar, la aplicación lee la sesión persistida en Windows Credential
Manager y la valida. El auto-montaje está habilitado mediante
`autoMountOnStartup`; solo se solicita cuando la validación resulta `Accepted`.
El coordinador evita repetir la solicitud para la misma sesión y no intenta
montar una unidad que ya esté montada.

Si la sesión falta o es rechazada, el montaje automático no se ejecuta y la
interfaz queda disponible para el flujo de autenticación. Los errores de red o
timeouts transitorios tienen reintentos limitados. Una respuesta de rechazo no
borra automáticamente la credencial guardada; el cierre de sesión explícito sí
la elimina. Por tanto, una sesión rechazada no debe interpretarse como una
autenticación válida ni como motivo para montar la unidad.

Credential Manager es el almacén persistente de la sesión de la aplicación.
Los registros de diagnóstico informan presencia y metadatos, no el contenido de
la credencial.

## Diagnóstico de autenticación

TEST8 añade el registro append-only
`%LOCALAPPDATA%\O2CloudDrive\Logs\auth-session.log`. Permanece en el perfil del
usuario entre cierres normales y reinicios. Registra hora, clasificación,
endpoint/método, estado HTTP, duración y presencia/cantidad de campos de sesión.
Para `Set-Cookie` conserva solo metadatos como nombre seguro, dominio, ruta,
expiración, `Max-Age` y atributos de seguridad; nunca guarda el valor de la
cookie, la ValidationKey ni el DeviceId.

Este archivo puede ayudar a distinguir sesión ausente, rechazo HTTP, campos
necesarios ausentes y fallos transitorios. Sigue siendo un registro local de
actividad: conviene revisarlo antes de compartirlo.

## Verificación real tras reinicio

El 7 de octubre de 2026 se comprobó un inicio de Windows posterior a la
eliminación de la entrada `HKCU Run`. La tarea se ejecutó a las 19:52:52; TEST8
inició una única instancia en esa misma sesión. La validación registrada a las
19:52:56 fue `Accepted` (HTTP 200), y `O:` y `O:\Fotos` quedaron accesibles sin
login ni montaje manual. En esa comprobación la tarea seguía en ejecución,
consistente con la aplicación que lanzó.

Resultado: **STARTUP/AUTH/AUTO-MOUNT TEST8 = FINAL PASS**. Esta observación
describe la instalación y el reinicio probados; no sustituye las pruebas
automatizadas de los cambios de código.
