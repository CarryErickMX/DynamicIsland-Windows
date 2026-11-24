# 🏝️ Dynamic Island para Windows

Una isla flotante estilo iPhone en la parte superior de tu pantalla. Muestra la hora, la batería, lo que estás escuchando y el volumen, con animaciones tipo resorte y diseño Liquid Glass.

<!-- Agrega tus capturas en docs/screenshots y descomenta estas líneas -->
<!--
| Oscuro | Claro |
|---|---|
| ![Oscuro](docs/screenshots/oscuro-expandida.png) | ![Claro](docs/screenshots/claro-expandida.png) |
-->

## ✨ Funciones
- **Vista compacta:** hora, batería (⚡ al cargar) y portada con ecualizador animado si hay música.
- **Al pasar el mouse:** se expande con portada, título, artista y controles ⏪ ▶ ⏩.
- **Volumen:** aparece al subir, bajar o silenciar. Se ajusta también con la rueda del mouse sobre la isla.
- **Avisos breves:** cambio de canción y conexión o desconexión del cargador.
- **Temas:** Automático (según Windows), Oscuro y Claro.
- **Discreta:** no aparece en la barra de tareas ni en Alt+Tab, y no roba el foco.
- **Compatible con** Spotify, YouTube en Edge o Chrome, Reproductor multimedia y cualquier app que use los controles multimedia de Windows.

## 📥 Instalación
1. Ve a [**Releases**](https://github.com/CarryErickMX/DynamicIsland-Windows/releases/latest) y descarga el instalador:
   - **`DynamicIsland-Setup-x64.exe`** para la mayoría de las PC (Intel / AMD).
   - **`DynamicIsland-Setup-arm64.exe`** para equipos ARM (Snapdragon).
2. Ábrelo y sigue el asistente: **Siguiente → Instalar → Finalizar**.

Listo. La isla se abre al terminar y arranca sola cada vez que enciendes la PC. No necesitas permisos de administrador ni instalar .NET.

> **Aviso de SmartScreen:** como el instalador no está firmado digitalmente, Windows puede mostrar *"Windows protegió su PC"*. Haz clic en **Más información → Ejecutar de todas formas**.

**Para actualizar**, descarga el instalador de la versión nueva y ejecútalo encima. Reemplaza la anterior y conserva tu tema.

**¿Prefieres sin instalar?** Descarga `DynamicIsland-Portable-x64.zip`, guarda el `.exe` en una carpeta fija y activa **clic derecho → Iniciar con Windows**.

## 🖱️ Uso
| Acción | Resultado |
|---|---|
| Pasar el mouse | Expande la isla |
| Rueda del mouse sobre la isla | Sube o baja el volumen |
| Clic en la bocina | Silencia o activa el sonido |
| Clic derecho | Tema, Iniciar con Windows, Salir |

## 💻 Requisitos
- Windows 10 versión 2004 o superior, o Windows 11.

## 🛠️ Compilar desde el código
Necesitas el [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/CarryErickMX/DynamicIsland-Windows.git
cd DynamicIsland-Windows/src/DynamicIsland
dotnet run
```

Para generar el `.exe`:
```powershell
dotnet publish -c Release -r win-x64
```

Si aparece el error `NU1100`, agrega el origen de paquetes:
```powershell
dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org
```

## 🗑️ Desinstalar
**Configuración → Aplicaciones → Aplicaciones instaladas → Dynamic Island → Desinstalar.**
Se cierra la isla, se quita del inicio de Windows y se borran sus preferencias.

## 📄 Licencia
[MIT](LICENSE)
