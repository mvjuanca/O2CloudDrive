# Historia de overwrite

Comparación mediante tags y `git show`, sin instalar versiones antiguas.

| Tag | Tramo de interés |
| --- | --- |
| v0.7-beta | Ya usa UploadFile sin ID de destino + retirada posterior del original con error silenciado |
| v0.8-beta | Caché de lectura e invalidación; mismo algoritmo de subida |
| v0.8.1-beta | Consulta de cambios en background; mismo reemplazo |
| v0.8.2-beta | Desactiva polling periódico y ajusta lecturas; mismo reemplazo |
| v0.8.3-beta | Keepalive de sesión y alternativas de lectura; mismo reemplazo |
| v0.8.4-beta | Instalador/interfaz/log de errores; conserva el flujo defectuoso |

La lógica está presente al menos desde 0.7; esta comparación no demuestra en qué versión anterior nació. No hay una implementación distinta de update de bytes por ID en los tags solicitados. Parece una limitación estructural del diseño crear-y-retirar, no una regresión introducida solo en 0.8.4. No se ha validado cada binario antiguo contra O2 real.

Reproducción de la comparación:

```text
git show v0.7-beta:src/O2CloudDrive/VirtualFileSystem/O2CloudFileStore.cs
git diff v0.7-beta v0.8.4-beta -- src/O2CloudDrive/VirtualFileSystem/O2CloudFileStore.cs
git show v0.8.4-beta:src/O2CloudDrive/Api/O2CloudApiClient.cs
```
