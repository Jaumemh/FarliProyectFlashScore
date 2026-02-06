# Flashscore Overlay - Aplicación WPF

Esta aplicación WPF permite visualizar los partidos de Flashscore.es en un overlay independiente que se mantiene siempre en primer plano.

## Características

- ✅ Navegador integrado con WebView2
- ✅ Inyección automática del script personalizado
- ✅ Interfaz moderna y oscura
- ✅ Overlay siempre en primer plano
- ✅ Actualización automática de partidos
- ✅ Ventana arrastrable y redimensionable

## Requisitos

1. **.NET 8.0 SDK** (o superior)
   - Descargar: https://dotnet.microsoft.com/download

2. **WebView2 Runtime**
   - Generalmente ya instalado en Windows 10/11
   - Si no: https://developer.microsoft.com/microsoft-edge/webview2/

## Instalación y Ejecución

### Opción 1: Desde Visual Studio

1. Abre Visual Studio 2022
2. Abre el proyecto `FlashscoreOverlay.csproj`
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

El ejecutable estará en: `bin\Release\net8.0-windows\win-x64\publish\FlashscoreOverlay.exe`

## Uso

1. **Iniciar la aplicación**: Se abrirá una ventana con Flashscore.es cargado
2. **Esperar carga**: El script se inyectará automáticamente
3. **Buscar partidos**: Navega por Flashscore normalmente
4. **Añadir al overlay**: Verás un botón circular rojo (📌) en cada partido
5. **Clic en el botón**: El partido se añadirá al overlay (el botón se pondrá azul)
6. **Gestionar overlay**: 
   - Arrastrar: Click y mantener en la barra de título
   - Cerrar: Click en la X roja
   - Refrescar: Click en 🔄
   - Anclar/Desanclar: Click en 📌

## Controles

- **🔄 Refrescar**: Recarga la página de Flashscore
- **📌 Pin**: Mantiene la ventana siempre en primer plano (activo por defecto)
- **− Minimizar**: Minimiza la ventana
- **✕ Cerrar**: Cierra la aplicación

## Estructura del Proyecto

```
FlashscoreOverlay/
├── FlashscoreOverlay.csproj    # Configuración del proyecto
├── App.xaml                     # Configuración de la aplicación
├── App.xaml.cs                  # Lógica de inicio
├── MainWindow.xaml              # Interfaz principal
├── MainWindow.xaml.cs           # Lógica del navegador y script
├── OverlayWindow.xaml           # Interfaz del overlay (futuro)
├── OverlayWindow.xaml.cs        # Lógica del overlay (futuro)
└── README.md                    # Este archivo
```

## Diferencias con el script de Tampermonkey

### Script Original (Tampermonkey)
- Crea overlays dentro de la misma página web
- Limitado al navegador
- Depende de la extensión Tampermonkey

### Aplicación WPF (Esta)
- Aplicación independiente de Windows
- Navegador integrado con WebView2
- No necesita extensiones del navegador
- Puede crear ventanas overlay independientes del sistema
- Siempre en primer plano
- Mayor control sobre la interfaz

## Próximas Mejoras

- [ ] Crear ventanas overlay independientes por competición
- [ ] Guardar posición y tamaño de ventanas
- [ ] Notificaciones de goles
- [ ] Filtros por competición
- [ ] Configuración de actualización automática
- [ ] Tema claro/oscuro

## Solución de Problemas

### Error: "WebView2 Runtime not found"
**Solución**: Instalar WebView2 Runtime desde https://developer.microsoft.com/microsoft-edge/webview2/

### Error: "El SDK de .NET no se encuentra"
**Solución**: Instalar .NET 8.0 SDK desde https://dotnet.microsoft.com/download

### La página no carga
**Solución**: 
1. Verificar conexión a internet
2. Click en el botón de refrescar (🔄)
3. Revisar firewall/antivirus

### Los botones no aparecen en los partidos
**Solución**:
1. Esperar 5-10 segundos después de la carga
2. Navegar a una sección con partidos (Fútbol > En vivo)
3. Click en refrescar si es necesario

## Contacto y Soporte

Para reportar problemas o sugerencias, contacta con el desarrollador.

## Licencia

Este proyecto es de uso educativo.
