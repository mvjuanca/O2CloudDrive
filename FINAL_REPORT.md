# O2 Cloud Drive — resultado del trabajo autónomo

Fecha: 04/10/2026. Workspace independiente: `C:\Users\mvjua\Documents\Proyectos\O2CloudDrive-overwrite-fix`.

## Respuestas finales

1. **Estado inicial real:** Windows x64 10.0.26200; sin proceso O2CloudDrive ni O: montada. Git 2.56.0. SDK .NET no disponible inicialmente; se preparó SDK oficial 8.0.425 portable en el workspace.
2. **WinFsp:** sí, 2.1.25156 instalado y launcher activo. No se instaló/reinstaló driver.
3. **Restos O2CloudDrive:** sí, Program Files/entrada 0.8.4 beta y carpetas LocalAppData; contradice una desinstalación completa, no el estado de aplicación inactiva. No se extrajeron credenciales ni se leyó config privada/cookies/validationkeys. No se reinstaló el cliente antiguo.
4. **Baseline:** HEAD upstream `5369cd42d9e8e43871a41297262e7b998db4b1e0` verificado al clonar; build PASS sin warnings/errores; self-test InMemory PASS. El repositorio MIT permaneció intacto hasta crear tests.
5. **Reproducción:** API falsa reproduce V1 original + V2 `(1)`, retirada fallida y éxito silencioso, misma longitud 17 B; antes 2/19 pruebas de regresión PASS. No es una operación actual contra O2 real.
6. **Causa raíz:** creación sin ID de destino; confirmación que permite fallback nombre/size; async Commit/dirty anticipado; catch de retirada y de upload silenciosos. Permite duplicación/inconsistencia; no se obtuvo el HTTP causal de la sesión histórica.
7. **Versiones afectadas por el código:** al menos 0.7, 0.8, 0.8.1, 0.8.2, 0.8.3 y 0.8.4. No se demuestra que todas las cuentas/archivos de esas versiones fallen igual.
8. **Overwrite nativo:** no identificado/validado en código, historia ni referencias revisadas. No se afirma que O2 carezca de una API privada. No se inventó endpoint/parámetro.
9. **Solución:** RemoteFileReplacement compartido: upload temporal, comprobación por ID/name/size/SHA-256, backup del original, promoción, reconfirmación, papelera del original. La retirada nunca precede a la comprobación de los nuevos bytes.
10. **Rollback:** inspecciona IDs reales, devuelve nuevo a temporal y viejo al nombre original cuando puede confirmarlo. Si es ambiguo/falla, conserva copia válida y dirty/journal, bloquea replay. No borra permanentemente ni autorrepara tras crash.
11. **Concurrencia:** lock por nodo compartido por Commit y mutadores; no upload en background; no `_gate` durante la transacción de red. Dos commits clean no duplican upload; cierre de lectores no sube writers; dos handles cierran con Commit en el último.
12. **Caché:** RemoteItem conserva datos originales; metadata nueva tras éxito; key de ReadBlocks cambia con ID/fingerprint. Caché dirty local sobrevive al fallo. No se purgan cachés de escritura/journals anteriores.
13. **Fake:** 28/28 casos Windows/backend/callback/contrato multipart, con fallos de upload/retirada/rename/confirmación y datos corruptos, rollback fallido, respuesta perdida y versión externa cambiada.
14. **Simulado:** 11/11 sobre montaje WinFsp R: + API falsa de colisiones; unidad desmontada. Self-test original InMemory también pasa.
15. **Regresión:** A–Q cubiertos en backend; tests ampliados R–AB. Core net8.0 sin UI/WinFsp: 25/25. Primer montaje detectó cero bytes sin Cleanup; se corrigió y volvió a pasar. Error Flush se traduce a IOException en Windows. **Cleanup/Close no ofrece un canal de error**, limitación documentada; no se presume que una aplicación reciba todos los fallos al cerrar sin Flush.
16. **Build:** Release portable self-contained Windows x64, `artifacts/0.8.4-juanca-test1/`, 476 archivos / 170.221.022 bytes. Compilada desde el commit de código 722d508, antes del commit únicamente documental final. No se generó instalador ni se instaló la app. Identificación 0.8.4-juanca-test1, no 0.8.5 oficial.
17. **SHA-256:** EXE launcher `7D3DFA475775B2CE840E7FED06A08CC486619BCEE067D67D40A48ED4C32C2A78`, 177.664 B. DLL que contiene el código `68E8B955CF0ED8F18D5AE3E140850A9A6FAF30E86BFE9ED59E234ED78C95E725`, 352.256 B. `artifacts/build-sha256.csv` verifica individualmente los binarios; no basta comparar solo el launcher. Recompilar desde otro commit puede cambiar metadatos/hash, aunque solo se hayan añadido documentos.

    ZIP portable `artifacts/O2CloudDrive-0.8.4-juanca-test1-win-x64.zip`: 72.920.478 B; SHA-256 `F8F872B3EA7F8AAF7399E6C3DA6D85FD0A4EB93FACA3F7A396EE4FFF3645417A`. Conserva LICENSE y runtime necesario; no incluye SDK, perfil login ni datos de cuenta.
18. **Patch:** `artifacts/o2clouddrive-overwrite-fix.patch`, diff binario completo desde HEAD upstream al HEAD local. Ignorado deliberadamente junto con builds/logs.
19. **Commits:** locales lógicos, enumerados íntegramente en `artifacts/commits.txt` y `git log`. Reproducción 63f7f62; fix abed38a; tests/portabilidad fea6a41; contrato HTTP 1d0d6cf; ajuste del interceptor 0e7e123; documentos finales en commit posterior. Sin push, fork, PR ni escritura en issue. Árbol versionado limpio al terminar.
20. **Linux:** sí, el fix es reutilizable; su clase no depende de Windows y el store se compila/prueba net8.0. No se construyó aplicación Linux.
21. **Partes reutilizables:** aproximadamente 51% de líneas en carpetas candidatas Core en la medición descrita en LINUX_PORTABILITY; no porcentaje de esfuerzo ni de port completado. API/store/caché/DTO/transferencias son candidatas; algunos componentes adicionales de auth/config también.
22. **Falta Linux:** FUSE3/binding/errno/handles, auth humana legítima/retorno, session store/keyring, packaging/rutas/permisos y tests reales de SO. No se instaló FUSE ni se accedió a la Pi.
23. **O2 real:** BLOCKED login humano nuevo. No se reutilizaron sesiones ni credenciales históricas y no se hicieron operaciones de archivos sobre la cuenta.
24. **WinFsp necesario:** sí para las pruebas montadas, y ya estaba instalado; el core/API no lo necesita.
25. **Login humano:** pendiente. Runner usa O2LoginForm normal con perfil nuevo aislado y sin Credential Manager, timeout 90 s. La comprobación al final es login-only: no monta ni muta archivos remotos aunque se autentique.
26. **V1→V2→V3 real:** BLOCKED, no realizado. PASS en los tests fake/simulados; no confundirlos con producción.
27. **`(1)` real:** antecedente histórico en issue; desaparece en el backend falso corregido. Ausencia en O2 real actual todavía no verificada.
28. **Refresh/remount real:** BLOCKED. Store nuevo y reopen offline PASS; runner preparado para montar/remontar únicamente la carpeta aislada después de un login humano.
29. **Uso Windows:** apto para prueba controlada pequeña, **no** recomendado todavía para datos de producción. Riesgos: consistencia eventual, doble descarga, cuotas/papelera, temporales de fallos, no atomicidad servidor/CAS, Cleanup sin resultado y rename replaceIfExists independiente aún sin transacción equivalente.
30. **PR:** borrador y plan preparados, no publicado. Listo para revisión técnica; no para merge como solución de producción hasta validar O2 real y revisar esos límites.

## Documentación y reproducibilidad

`docs/BASELINE.md`, `WRITE_PIPELINE.md`, `OVERWRITE_HISTORY.md`, `OVERWRITE_ROOT_CAUSE.md`, `OVERWRITE_FIX.md`, `OVERWRITE_TEST_MATRIX.md`, `LINUX_PORTABILITY.md`, `CROSS_PLATFORM_ARCHITECTURE.md`, `UPSTREAM_ISSUE_FOLLOWUP_DRAFT.md`, `UPSTREAM_PR_PLAN.md`.

`tools/Build-Test.ps1` repite tests/build/manifiestos/patch. Requiere SDK .NET 8 y WinFsp para el segmento montado. No solicita refresh de OVMS, no toca Home Assistant, RadioAdmin, MQTT, la radio, Git central ni OneDrive canónico.

Prueba real futura preparada: `artifacts/real-validation-runner/RealValidation.exe --validate`, con login humano y R: libre. Alcance exclusivo `O2CloudDrive-Overwrite-Test-20261004`, guard de IDs en toda mutación/download; preserva evidencia y se niega a pisar un run anterior. No ejecutar sobre carpetas ajenas ni eliminar temporales sin revisar journals.

## Conclusión

Comprobación final de autenticación: a las 19:52:40 CEST se abrió la ventana `O2 Cloud - Login` del runner login-only, PID 10680, respondiendo, con perfil nuevo. Registro: `artifacts/real-validation-runner/runs/20261004-195240-3075790f51cd41c99ca06a5b046fe815/real-validation.log`. Tiempo máximo 90 s; no montaje ni escritura remota habilitados. No se ha completado una validación de archivos O2 reales en esta tarea.

CORRECCIÓN IMPLEMENTADA Y VALIDADA OFFLINE; FALTA PRUEBA CONTRA O2 REAL

TODO EL TRABAJO OFFLINE COMPLETADO; FALTA LOGIN HUMANO PARA VALIDACIÓN REAL

O2 CLOUD DRIVE: CORRECCIÓN IMPLEMENTADA Y VALIDADA OFFLINE; PENDIENTE VALIDACIÓN CONTRA O2 REAL
