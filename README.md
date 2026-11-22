# AORUS Monitor

Monitor y centro de control ligero para el **GIGABYTE AORUS 15G XC**, sin instalar Gigabyte Control Center.
No toca frecuencias, voltajes ni límites de potencia del procesador.

## Funciones

- **Panel**: temperatura, carga, frecuencia y consumo de CPU y GPU; RPM de ventiladores; RAM; batería; discos.
- **Bandeja del sistema**: iconos con la temperatura de CPU (naranja) y GPU (verde), que pasan a ámbar desde 80 °C y a rojo desde 90 °C.
  Clic izquierdo abre la ventana. Con el botón derecho: mini monitor, modo de ventiladores, efectos y brillo de las luces, iniciar con Windows y salir.
- **Mini monitor**: ventanita flotante semitransparente y siempre encima con CPU, GPU, ventiladores y memoria.
  Se arrastra, recuerda su posición, doble clic abre el panel y el botón derecho permite cambiar la opacidad.
- **Ventiladores**: modos Normal, Silencioso, Gaming, Turbo, Manual y Curva personalizada (editor arrastrable).
  Protección: a ≥95 °C durante 5 s pone los ventiladores al máximo hasta bajar de 80 °C.
- **Iluminación**: 13 efectos de hardware del teclado (neón arcoíris, ola, marquesina, lluvia, ondas…) y color RGB libre.
- **Inicio con Windows** en la bandeja. Al minimizar queda en la barra de tareas; al cerrar la ventana, sigue en la bandeja.

## Instalación

`Installer\salida\AorusMonitor-<versión>-instalador.exe`:

- Instala en `C:\Program Files\AORUS Monitor` y crea accesos directos en el escritorio y en el menú Inicio.
- Registra la interfaz WMI de ventiladores (`Wmi\gb_wmiacpi.mof`, sin software de Gigabyte).
- Instala el driver PawnIO si falta (frecuencia, consumo y núcleos de la CPU).
- Crea dos tareas programadas elevadas: «AORUS Monitor» (al iniciar sesión, en la bandeja) y «AORUS Monitor (abrir)».
  La app se relanza a sí misma con esta última, así que siempre se ejecuta como administrador sin pedir permiso.

El desinstalador devuelve los ventiladores a Normal, quita la interfaz WMI y las tareas.

## Cómo funciona

| Parte | Interfaz |
|---|---|
| Ventiladores y RPM | ACPI-WMI de la BIOS (`root\WMI`, `GB_WMIACPI_Get/Set`). Las RPM vienen con los bytes invertidos. |
| Teclado RGB | HID del controlador ITE 829x (USB 1044:7A3B, interfaz 3): feature reports de 9 bytes. Protocolo de [AeroCtl](https://gitlab.com/wtwrp/aeroctl). |
| Sensores | [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) con el driver PawnIO + temperaturas del EC por WMI como respaldo. |

## Compilar

```powershell
powershell -ExecutionPolicy Bypass -File Installer\compilar.ps1
```

Gráficos (icono e imágenes del instalador): `Tools\generar-graficos.ps1`.

Opciones: `--minimizado`, `--arranque si|no`, `--tarea-abrir si|no`, `--restaurar`, `--probe archivo` (diagnóstico de solo lectura), `--snapshot carpeta` (capturas de la interfaz).

## Licencia

[MIT](LICENSE). Se usa bajo tu responsabilidad: controla los ventiladores a través de la BIOS y no está afiliado a GIGABYTE.
