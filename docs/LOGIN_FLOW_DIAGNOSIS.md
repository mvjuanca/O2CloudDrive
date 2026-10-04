# Diagnóstico del acceso web O2 Cloud

Fecha: 04/10/2026 (CEST). Alcance: diagnóstico de la landing y autenticación; ninguna operación de archivos remotos.

## Hallazgos

- El destino estable de entrada sigue siendo `https://cloud.o2online.es/`; no se fijó ninguna URL interna de identidad ni se conservó ningún parámetro OIDC dinámico.
- Una inspección web previa de la landing pública `/login` mostró el QR/promoción de la app junto con un botón web legítimo `Acceder`. El mismo flujo incluía un acceso separado por email/contraseña. La documentación oficial de O2 describe el acceso web mediante número móvil y código SMS ([condiciones particulares de O2 Cloud](https://web.static.o2online.es/media/public/condiciones_particulares_o2_cloud.pdf)).
- La ejecución de `test2` reprodujo la navegación en un perfil WebView2 temporal nuevo. La política local solo pulsó el botón `Acceder` cuando coincidían host HTTPS exacto, ruta `/login`, encabezado de la landing, un único botón visible con ese texto y ausencia de diálogo/banner de consentimiento. El registro confirma `WEB_ACCESS_CTA_CLICKED`.
- La navegación observada siguió las rutas oficiales dinámicas de O2/Telefónica: raíz O2 Cloud → autorización PKCE de O2 Cloud → autorización de identidad Telefónica → `t3.o2online.es/acceso/#/accessUserPassO2`. Los valores de query/fragmento se redactan en los logs y no se documentan aquí.
- En la ruta `t3`, el título observado fue `Login App O2`, con campos DOM de tipos `text` y `password`; no apareció la promoción QR/app en los indicadores de la captura. En esta ejecución los flags de texto `phonePrompt` y `smsPrompt` quedaron en `false`; por tanto, el log técnico por sí solo no prueba literalmente las etiquetas mostradas. Una inspección visual/DOM anterior del mismo flujo O2 mostró el prompt de número móvil y envío de código SMS, pero no se reutilizaron parámetros de aquella sesión.
- Después hubo retorno a O2 Cloud y `O2SessionValidator` aceptó una sesión en memoria. No se registra si la interacción fue manual ni se almacena el teléfono, PIN, cookies o validation key. El primer GET de espacio (`GetStorageInfo`) terminó con `HttpRequestException`; el runner abortó antes de listar la raíz. No hubo montaje ni escritura remota. El perfil WebView temporal se eliminó correctamente.

## Causa y límites

La explicación respaldada para la pantalla QR es que la landing pública es promocional y el flujo web requiere seguir su botón `Acceder`; el código previo no realizaba esa elección de forma controlada. No hay evidencia de que O2 detecte WebView2 como causa. El WebView2 usa el User-Agent Edge instalado y la aplicación no lo sustituye; no se cambió.

Se compararon respuestas HTTP de la raíz usando cadenas User-Agent Edge y Firefox, pero no una sesión interactiva completa en Firefox/Edge. La automatización disponible no podía controlar de forma segura navegadores nativos; la pestaña del navegador integrado llegó a mostrar contenido de una sesión autenticada del usuario, así que se cerró esa pestaña diagnóstica y no se inspeccionó ni reutilizó dicho contenido. No se debe inferir equivalencia completa entre navegador normal y WebView2.

El código de consentimiento quedó acotado a controles identificados como banner/dialog de cookies. Los popups HTTPS de subdominios O2 se atienden en el mismo WebView; las tiendas de aplicaciones y orígenes externos se bloquean. Los diagnósticos solo registran ruta saneada, título, User-Agent, viewport, indicadores booleanos, tipos de input y estados HTTP GET; nunca texto libre del formulario o valores de campos.

## Evidencia de la ejecución

Registro sanitizado: `artifacts/real-validation-runner-test2/runs/20261004-210323-acc86f3751ee4d4991d094b331f1c72f/real-validation.log`.

El registro confirma apertura con perfil nuevo y sin timeout de 90 s, click del CTA, ruta oficial de identidad, campos `text`/`password`, retorno OAuth, validación de sesión en memoria, eliminación del perfil temporal y fallo de la primera lectura de espacio. No contiene credenciales ni valores OIDC. El proceso terminó al completarse el flujo de sesión; por ello ya no hay una ventana de login abierta para que el usuario continúe.

## Implementación y pruebas

- `O2LoginFlowPolicy` permite únicamente la transición concreta y segura desde la landing O2 al CTA web; no codifica el endpoint dinámico de SSO.
- `O2LoginForm` conserva navegación oficial, limita cookies, enruta popups de O2 y bloquea navegación a tiendas.
- El ejecutor humano elimina su perfil temporal antes de llamar a la API y no usa Credential Manager ni perfiles previos. Se quitó el cierre automático a los 90 s.
- Tests del flujo: 12/12 PASS. Tests de sobrescritura existentes: 28/28 PASS. Core: 25/25 PASS. Build portable `0.8.4-juanca-test2` completada.

La autenticación web llegó a producir una sesión aceptada por el validador, pero el acceso de lectura a la API todavía no está validado. No continuar a pruebas de sobrescritura real hasta resolver y superar las lecturas de espacio y raíz.
