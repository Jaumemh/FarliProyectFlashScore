# Flashscore Overlay - Aplicación WPF

Esta aplicación WPF actúa como un host WebView2 que abre cada partido seleccionado de Flashscore.es en ventanas flotantes independientes, siempre encima del resto de aplicaciones.

## Características

- ✓ Ventanas individuales por partido usando Microsoft Edge (Chromium) a través de WebView2.
- ✓ Cada overlay aísla el contenedor exacto del partido y elimina el resto del DOM.
- ✓ Actualización interna cada 20 segundos mediante `fetch` en segundo plano.
- ✓ Ventanas borderless, topmost, ajustadas al tamaño real del HTML del partido.
- ✓ Comunicación directa con un script de Tampermonkey para abrir/cerrar ventanas al hacer clic en los partidos.
- ✓ Clic derecho dentro del overlay cierra esa ventana sin tocar las demás.

## Arquitectura de la solución

1. **App WPF**: el `MainWindow` se oculta al iniciar y expone un servidor HTTP local en `http://localhost:8080/` para escuchar comandos desde el navegador.
2. **TampermonkeyScript.js**: inserta un botón (📑) en cada partido de Flashscore, detecta `matchId`, `matchMid` y la URL del evento, y manda el payload al servidor cuando se pulsa.
3. **OverlayWindow**: cada comando `addMatch` crea una ventana WebView2 separada que carga la URL real, ejecuta un script para aislar solo el nodo correcto y reajusta su tamaño automáticamente.

## Requisitos

1. **.NET 8.0 SDK** (o superior)
   - Descargar: https://dotnet.microsoft.com/download

2. **WebView2 Runtime**
   - Generalmente ya viene instalado en Windows 10/11
   - Si falta: https://developer.microsoft.com/microsoft-edge/webview2/

## Instalación y Ejecución

### Opción 1: Desde Visual Studio

1. Abre Visual Studio (2022 o 2023)
2. Carga `FlashscoreOverlay.csproj`
3. Presiona F5 o clic en "Iniciar"

### Opción 2: Desde la terminal

```powershell
cd "c:\Users\jaume\Documents\DAM2\SGE\FlashscoreOverlay"
dotnet restore
dotnet build
dotnet run
```

### Opción 3: Compilar ejecutable

```powershell
cd "c:\Users\jaume\Documents\DAM2\SGE\FlashscoreOverlay"
dotnet publish -c Release -r win-x64 --self-contained false
```

El ejecutable queda en: `bin\Release\net8.0-windows\win-x64\publish\FlashscoreOverlay.exe`

## Uso

1. Ejecuta la aplicación WPF; la ventana principal se ocultará, pero el servidor HTTP quedará activo y escuchando en `localhost:8080`.
2. Instala y activa `TampermonkeyScript.js` en Flashscore.es (el archivo está junto a este README) y abre una sección de partidos.
3. Cada partido muestra un botón rojo (📑). Haz clic para abrir un overlay flotante: la ventana carga la URL real y muestra exclusivamente el contenedor del partido.
4. El botón cambia a azul mientras el overlay esté abierto; vuelve a pulsarlo para cerrarlo (toggle).
5. Puedes abrir tantos partidos como quieras; cada uno vive en su propio overlay y se actualiza automáticamente.

## Overlay WebView2

- Cada overlay tiene un header pequeño con título, botón de recarga (↺) y cierre (✕). Arrastra desde el encabezado para moverlo.
- Dentro del WebView2 se ejecuta un script que detecta el `matchId` y/o el `matchMid`, elimina el resto del DOM y mantiene solo el nodo deseado.
- Se exportan los bounds del nodo al host para ajustar el ancho/alto de la ventana y conservar el mismo tamaño que en Flashscore.
- Un `setInterval` cada 20 segundos dispara un `fetch` sobre la misma URL y actualiza el HTML interno del nodo, sin recargar toda la vista.
- Un clic derecho dentro del contenido manda un mensaje a la app para cerrar solo ese overlay, sin afectar a los demás.
- Las ventanas comparten sesión WebView2 por defecto, así que cookies e inicio de sesión se mantienen.

## Controles

- **↺ Recargar**: recarga la misma URL dentro del overlay.
- **✕ Cerrar**: cierra ese overlay individual.
- **Botón Tampermonkey (📑)**: toggle para abrir/cerrar el overlay correspondiente.
- **Clic derecho dentro del overlay**: también cierra la ventana activa.
- **MainWindow**: sirve como panel de estado si decides visualizarlo (muestra el servidor y el contador de overlays activos).

## Estructura del Proyecto

```
FlashscoreOverlay/
├── FlashscoreOverlay.csproj    # Configuración del proyecto
├── App.xaml                     # Configuración de la aplicación
├── App.xaml.cs                  # Punto de entrada WPF
├── MainWindow.xaml              # Interfaz de estado / servidor HTTP
├── MainWindow.xaml.cs           # Lógica del servidor y gestión de overlays
├── OverlayWindow.xaml           # Diseño de cada ventana overlay
├── OverlayWindow.xaml.cs        # Lógica WebView2 + aislamiento
├── TampermonkeyScript.js        # Script del navegador
└── README.md                    # Esta documentación
```

## Tampermonkey & comunicación

El script realiza lo siguiente:

1. Añade un botón dentro de cada `.event__match`.
2. Al hacer clic envía `matchId`, `matchMid`, la URL y parte del HTML al servidor local.
3. El servidor responde con `{ status: "ok" }` y abre/cierra la ventana correspondiente. Si el overlay ya existía, se cierra (toggle).

## Solución de Problemas

### Error: "WebView2 Runtime not found"
Instala WebView2 Runtime desde https://developer.microsoft.com/microsoft-edge/webview2/

### Error: "El SDK de .NET no se encuentra"
Instala .NET 8.0 SDK desde https://dotnet.microsoft.com/download

### El overlay no muestra el partido
1. Asegúrate de que Flashscore esté accesible.
2. Pulsa el botón ↺ dentro del overlay o vuelve a hacer clic en el botón 📑.
3. Revisa que el firewall no esté bloqueando `localhost:8080`.

## Contacto y Soporte

Para reportar problemas o sugerencias, contacta con el desarrollador.

## Licencia

Este proyecto es de uso educativo.
