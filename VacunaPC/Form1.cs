using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace VacunaPC
{
    public partial class Form1 : Form
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        // Instalación portable
        private const string INSTALL_DIR = @"C:\VACUNAUNEZ";
        private const string INSTALL_EXE = "VacunaPC.exe";
        private const string RUN_KEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string RUN_VALUE = "VacunaPC";

        // Créditos
        private const string AUTOR = "Ing. Abraham Jaziel Gómez Lozano";

        private static Mutex? instanciaUnica;

        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private System.Windows.Forms.Timer timerUnidades = null!;
        private readonly HashSet<string> unidadesAnalizadas =
            new(StringComparer.OrdinalIgnoreCase);

        // Discos fijos que ya estaban conectados al iniciar (no se consideran "nuevos").
        private readonly HashSet<string> unidadesFijasInicio =
            new(StringComparer.OrdinalIgnoreCase);

        // Resultado de la consulta de bus USB por unidad.
        private readonly Dictionary<string, bool> cacheUsb =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim scanLock = new(1, 1);

        private readonly string basePath =
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
                "VacunaPC");

        private string QuarantinePath =>
            Path.Combine(basePath, "Cuarentena");

        private string LogPath =>
            Path.Combine(basePath, "actividad.log");

        private bool allowExit = false;

        public Form1()
        {
            // Si no está instalado: pide administrador, se copia a
            // C:\VACUNAUNEZ, se agrega al inicio de Windows y se reinicia desde ahí.
            if (!VerificarInstalacion())
            {
                Environment.Exit(0);
                return;
            }

            InitializeComponent();
            InitializeVaccine();
        }

        // ------------------------------------------------------------
        //  Instalación portable en C:\VACUNAUNEZ + inicio con Windows
        // ------------------------------------------------------------

        // Devuelve true si debe continuar la ejecución normal.
        private static bool VerificarInstalacion()
        {
            string actual = Environment.ProcessPath ?? Application.ExecutablePath;
            string destino = Path.Combine(INSTALL_DIR, INSTALL_EXE);

            bool yaInstalado = string.Equals(
                Path.GetFullPath(actual),
                Path.GetFullPath(destino),
                StringComparison.OrdinalIgnoreCase);

            if (!yaInstalado)
            {
                bool admin;

                using (WindowsIdentity identidad = WindowsIdentity.GetCurrent())
                {
                    admin = new WindowsPrincipal(identidad)
                        .IsInRole(WindowsBuiltInRole.Administrator);
                }

                if (!admin)
                {
                    // Pedir permisos de administrador (UAC) y relanzar.
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = actual,
                            UseShellExecute = true,
                            Verb = "runas",
                            WorkingDirectory = Path.GetDirectoryName(actual)!
                        });
                    }
                    catch (Win32Exception)
                    {
                        MessageBox.Show(
                            "VacunaPC necesita permisos de administrador para instalarse.",
                            "VacunaPC",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }

                    return false;
                }

                try
                {
                    Instalar(actual, destino);

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = destino,
                        WorkingDirectory = INSTALL_DIR,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "No se pudo instalar VacunaPC:\n" + ex.Message,
                        "VacunaPC",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }

                return false;
            }

            // Instancia única (ya instalado).
            instanciaUnica = new Mutex(
                true, @"Global\VacunaPC_UNEZ", out bool esNueva);

            return esNueva;
        }

        private static void Instalar(string origen, string destino)
        {
            Directory.CreateDirectory(INSTALL_DIR);

            // Cerrar una instancia anterior que corra desde C:\VACUNAUNEZ.
            foreach (Process p in Process.GetProcessesByName(
                         Path.GetFileNameWithoutExtension(INSTALL_EXE)))
            {
                try
                {
                    if (p.Id == Environment.ProcessId)
                        continue;

                    if (string.Equals(p.MainModule?.FileName, destino,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }

            // Carpeta de datos (cuarentena y registro): permitir escritura a todos
            // los usuarios, porque la crea esta instancia elevada.
            string datos = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData),
                "VacunaPC");

            Directory.CreateDirectory(datos);

            DirectoryInfo infoDatos = new DirectoryInfo(datos);
            DirectorySecurity seguridad = infoDatos.GetAccessControl();

            seguridad.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            infoDatos.SetAccessControl(seguridad);

            File.Copy(origen, destino, true);

            // Archivos de apoyo (si no se publicó como archivo único).
            foreach (string archivo in Directory.GetFiles(
                         Path.GetDirectoryName(origen)!))
            {
                string ext = Path.GetExtension(archivo).ToLowerInvariant();

                if (ext is ".dll" or ".json" or ".config")
                {
                    File.Copy(
                        archivo,
                        Path.Combine(INSTALL_DIR, Path.GetFileName(archivo)),
                        true);
                }
            }

            // Inicio con Windows para todos los usuarios.
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);

            using RegistryKey run = baseKey.OpenSubKey(RUN_KEY, true)
                ?? throw new InvalidOperationException(
                    "No se pudo abrir la clave de inicio de Windows.");

            run.SetValue(RUN_VALUE, $"\"{destino}\"", RegistryValueKind.String);
        }

        // ------------------------------------------------------------
        //  Inicialización (interfaz de bandeja mejorada)
        // ------------------------------------------------------------

        private void InitializeVaccine()
        {
            Directory.CreateDirectory(basePath);
            Directory.CreateDirectory(QuarantinePath);

            trayMenu = new ContextMenuStrip
            {
                Font = new Font("Segoe UI", 9.5f),
                ShowImageMargin = false,
                BackColor = Color.FromArgb(31, 41, 55),
                ForeColor = Color.FromArgb(229, 231, 235)
            };

            ToolStripMenuItem encabezado =
                new ToolStripMenuItem("VacunaPC · Protección USB")
                {
                    Enabled = false,
                    Font = new Font("Segoe UI", 10, FontStyle.Bold)
                };

            trayMenu.Items.Add(encabezado);
            trayMenu.Items.Add(new ToolStripSeparator());

            trayMenu.Items.Add("Analizar USB ahora",
                null, async (s, e) =>
                    await AnalyzeUSBAsync(true));

            trayMenu.Items.Add("Abrir cuarentena",
                null, (s, e) =>
                    OpenFolder(QuarantinePath));

            trayMenu.Items.Add("Ver registro",
                null, (s, e) =>
                    OpenFile(LogPath));

            trayMenu.Items.Add(new ToolStripSeparator());

            trayMenu.Items.Add("Acerca de",
                null, (s, e) =>
                    ShowAbout());

            trayMenu.Items.Add("Salir",
                null, (s, e) =>
                    ExitApplication());

            trayMenu.Items.Add(new ToolStripSeparator());

            trayMenu.Items.Add(new ToolStripMenuItem(
                "Desarrollado por " + AUTOR)
            {
                Enabled = false
            });

            trayIcon = new NotifyIcon()
            {
                Icon = SystemIcons.Shield,
                Text = "VacunaPC - Sistemas UNEZ",
                ContextMenuStrip = trayMenu,
                Visible = true
            };

            trayIcon.DoubleClick += async (s, e) =>
                await AnalyzeUSBAsync(true);

            // Discos fijos presentes al iniciar: solo se analizan si son USB
            // o si aparecen después.
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType == DriveType.Fixed && d.IsReady)
                        unidadesFijasInicio.Add(d.RootDirectory.FullName);
                }
                catch { }
            }

            // Respaldo: revisa cada 2 s si hay unidades USB nuevas, sin depender
            // solo del aviso de Windows (la unidad puede tardar en montarse).
            timerUnidades = new System.Windows.Forms.Timer { Interval = 2000 };
            timerUnidades.Tick += (s, e) => RevisarUnidades();
            timerUnidades.Start();

            Log("VacunaPC iniciada correctamente.");
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // Ejecutar análisis inicial después de iniciar Windows Forms.
            BeginInvoke(new Action(async () =>
                await AnalyzeUSBAsync(false)));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // Mantener el programa en segundo plano.
            Hide();
            ShowInTaskbar = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE)
            {
                int eventType = m.WParam.ToInt32();

                if (eventType == DBT_DEVICEARRIVAL)
                {
                    Log("Dispositivo USB conectado.");

                    // Esperar a que Windows monte la memoria; si aún no está
                    // lista, el temporizador la detectará en cuanto lo esté.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);

                        if (!IsDisposed && IsHandleCreated)
                        {
                            BeginInvoke(new Action(RevisarUnidades));
                        }
                    });
                }

                if (eventType == DBT_DEVICEREMOVECOMPLETE)
                {
                    Log("Dispositivo USB desconectado.");
                }
            }

            base.WndProc(ref m);
        }

        // ------------------------------------------------------------
        //  Tipo de unidad (memorias USB y discos duros externos)
        // ------------------------------------------------------------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            int nInBufferSize,
            byte[] lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        // Consulta a Windows el tipo de bus de la unidad (7 = USB).
        private static bool EsBusUsb(string root)
        {
            try
            {
                // "E:\" -> "\\.\E:"
                string ruta = @"\\.\" + root.Substring(0, 2);

                using SafeFileHandle h = CreateFile(
                    ruta, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);

                if (h.IsInvalid)
                    return false;

                // STORAGE_PROPERTY_QUERY: StorageDeviceProperty + consulta estándar.
                byte[] consulta = new byte[12];
                byte[] salida = new byte[1024];

                // IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400
                if (!DeviceIoControl(h, 0x002D1400,
                        consulta, consulta.Length,
                        salida, salida.Length,
                        out int devueltos, IntPtr.Zero) ||
                    devueltos < 32)
                    return false;

                // STORAGE_DEVICE_DESCRIPTOR.BusType está en el offset 28.
                return BitConverter.ToInt32(salida, 28) == 7;
            }
            catch
            {
                return false;
            }
        }

        // Devuelve el motivo por el que se analiza la unidad, o null si no corresponde.
        private string? TipoAnalizable(DriveInfo drive)
        {
            if (drive.DriveType == DriveType.Removable)
                return "removible";

            if (drive.DriveType != DriveType.Fixed)
                return null;

            string root = drive.RootDirectory.FullName;

            // Nunca el disco del sistema.
            if (string.Equals(root,
                    Path.GetPathRoot(Environment.SystemDirectory),
                    StringComparison.OrdinalIgnoreCase))
                return null;

            if (!cacheUsb.TryGetValue(root, out bool usb))
            {
                usb = EsBusUsb(root);
                cacheUsb[root] = usb;
            }

            if (usb)
                return "disco externo USB";

            // Respaldo: disco fijo que apareció después de iniciar el programa.
            if (!unidadesFijasInicio.Contains(root))
                return "disco nuevo";

            return null;
        }

        // Detecta unidades USB nuevas y lanza el análisis automático.
        // Si hay un análisis en curso, la unidad queda pendiente y se
        // analiza en la siguiente revisión (cada 2 s).
        private void RevisarUnidades()
        {
            if (IsDisposed)
                return;

            var actuales = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            var fijasPresentes = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            bool hayNuevas = false;

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if ((drive.DriveType != DriveType.Removable &&
                         drive.DriveType != DriveType.Fixed) ||
                        !drive.IsReady)
                        continue;

                    string root = drive.RootDirectory.FullName;

                    if (drive.DriveType == DriveType.Fixed)
                        fijasPresentes.Add(root);

                    if (TipoAnalizable(drive) == null)
                        continue;

                    actuales.Add(root);

                    if (!unidadesAnalizadas.Contains(root))
                        hayNuevas = true;
                }
                catch { }
            }

            // Olvidar unidades retiradas para analizarlas al reconectarlas.
            unidadesAnalizadas.IntersectWith(actuales);

            // Un disco fijo retirado y vuelto a conectar cuenta como nuevo.
            unidadesFijasInicio.IntersectWith(fijasPresentes);

            foreach (string k in cacheUsb.Keys
                         .Where(k => !fijasPresentes.Contains(k)).ToList())
                cacheUsb.Remove(k);

            if (hayNuevas)
                _ = AnalyzeUSBAsync(false);
        }

        private async Task AnalyzeUSBAsync(bool manual)
        {
            if (!await scanLock.WaitAsync(0))
                return;

            try
            {
                int total = 0;
                var detected = new List<string>();

                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if ((drive.DriveType != DriveType.Removable &&
                             drive.DriveType != DriveType.Fixed) ||
                            !drive.IsReady)
                            continue;

                        string? tipo = TipoAnalizable(drive);

                        if (tipo == null)
                            continue;

                        string root = drive.RootDirectory.FullName;

                        // En el análisis automático solo se revisan unidades nuevas.
                        if (!manual && unidadesAnalizadas.Contains(root))
                            continue;

                        unidadesAnalizadas.Add(root);

                        Log($"Analizando unidad ({tipo}): {root}");

                        int result = await Task.Run(() =>
                            ScanDrive(root));

                        Log($"Análisis de {root} terminado: {result} coincidencia(s).");

                        if (result > 0)
                        {
                            total += result;
                            detected.Add(root);
                        }
                    }
                    catch (Exception ex) when (
                        ex is IOException ||
                        ex is UnauthorizedAccessException)
                    {
                        Log("Error leyendo unidad: " + ex.Message);
                    }
                }

                if (total > 0)
                {
                    trayIcon.ShowBalloonTip(
                        10000,
                        "¡Amenaza USB detectada!",
                        $"Se encontraron {total} coincidencias sospechosas. " +
                        $"Unidades: {string.Join(", ", detected)}. " +
                        "Los ejecutables fueron aislados.",
                        ToolTipIcon.Warning);
                }
                else if (manual)
                {
                    trayIcon.ShowBalloonTip(
                        5000,
                        "VacunaPC",
                        "Análisis terminado. No se encontraron coincidencias.",
                        ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                Log("Error de análisis: " + ex);
            }
            finally
            {
                scanLock.Release();
            }
        }

        private int ScanDrive(string root)
        {
            int repaired = 0;
            Stack<string> directories = new();
            directories.Push(root);

            int visited = 0;
            const int MAX_DIRECTORIES = 15000;

            while (directories.Count > 0 && visited < MAX_DIRECTORIES)
            {
                string current = directories.Pop();
                visited++;

                string[] subdirectories;
                try
                {
                    subdirectories = Directory.GetDirectories(current);
                }
                catch
                {
                    continue;
                }

                foreach (string folder in subdirectories)
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(folder);

                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            continue;

                        // Seguir explorando la jerarquía hacia abajo
                        directories.Push(folder);

                        string folderName = Path.GetFileName(folder);
                        string executablePath = Path.Combine(current, folderName + ".exe");
                        string lnkPath = Path.Combine(current, folderName + ".lnk");

                        bool hasMaliciousExe = File.Exists(executablePath);
                        bool hasMaliciousLnk = File.Exists(lnkPath);

                        // DETECCIÓN MEJORADA: Si coexisten la carpeta y un .exe o .lnk con el mismo nombre.
                        // Ya no dependemos de si la carpeta está oculta o visible para atrapar al virus.
                        if (hasMaliciousExe || hasMaliciousLnk)
                        {
                            Log($"¡Amenaza por suplantación detectada en: {folder}!");

                            if (hasMaliciousExe)
                            {
                                if (MoveToQuarantine(executablePath, root))
                                    repaired++;
                            }

                            if (hasMaliciousLnk)
                            {
                                if (MoveToQuarantine(lnkPath, root))
                                    repaired++;
                            }

                            // Forzar la limpieza de atributos ocultos/sistema de la carpeta legítima del docente
                            RestoreAttributes(folder);
                            repaired++;

                            Log("Carpeta legítima restaurada y desocultada: " + folder);
                        }
                    }
                    catch (Exception ex) when (
                        ex is IOException ||
                        ex is UnauthorizedAccessException ||
                        ex is SecurityException)
                    {
                        Log("Error procesando directorio: " + ex.Message);
                    }
                }
            }

            return repaired;
        }

        private void RestoreAttributes(string root)
        {
            Stack<string> pending = new();
            pending.Push(root);

            int visited = 0;

            while (pending.Count > 0 && visited < 15000)
            {
                string current = pending.Pop();
                visited++;

                try
                {
                    RemoveHiddenAttributes(current);

                    foreach (string file in
                        Directory.GetFiles(current))
                    {
                        try
                        {
                            RemoveHiddenAttributes(file);
                        }
                        catch (Exception ex)
                        {
                            Log("No se pudo desocultar: " + file +
                                " (" + ex.Message + ")");
                        }
                    }

                    foreach (string directory in
                        Directory.GetDirectories(current))
                    {
                        try
                        {
                            FileAttributes attr =
                                File.GetAttributes(directory);

                            if ((attr &
                                FileAttributes.ReparsePoint) != 0)
                                continue;

                            pending.Push(directory);
                        }
                        catch { }
                    }
                }
                catch
                {
                    Log("No se pudo restaurar: " + current);
                }
            }
        }

        private static void RemoveHiddenAttributes(string path)
        {
            FileAttributes attributes =
                File.GetAttributes(path);

            attributes &= ~FileAttributes.Hidden;
            attributes &= ~FileAttributes.System;

            if (attributes == 0)
                attributes = FileAttributes.Normal;

            File.SetAttributes(path, attributes);
        }

        private bool MoveToQuarantine(
            string executable,
            string drive)
        {
            try
            {
                string driveName =
                    drive.TrimEnd('\\').Replace(':', '_');

                string destinationFolder = Path.Combine(
                    QuarantinePath,
                    driveName,
                    DateTime.Now.ToString(
                        "yyyyMMdd_HHmmss_fff"));

                Directory.CreateDirectory(destinationFolder);

                string destination = Path.Combine(
                    destinationFolder,
                    Path.GetFileName(executable));

                int counter = 1;

                while (File.Exists(destination))
                {
                    destination = Path.Combine(
                        destinationFolder,
                        $"{Path.GetFileNameWithoutExtension(executable)}_{counter++}.exe");
                }

                // Estos virus marcan el .exe como oculto/sistema/solo lectura,
                // y eso impide moverlo (copiar + borrar) fuera de la USB.
                File.SetAttributes(executable, FileAttributes.Normal);

                File.Move(executable, destination);

                Log("Ejecutable aislado: " + destination);

                return true;
            }
            catch (Exception ex)
            {
                Log("Error de cuarentena: " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------
        //  Acerca de (interfaz mejorada)
        // ------------------------------------------------------------

        private void ShowAbout()
        {
            Color fondo = Color.FromArgb(17, 24, 39);
            Color panel = Color.FromArgb(31, 41, 55);
            Color acento = Color.FromArgb(14, 165, 233);
            Color texto = Color.FromArgb(229, 231, 235);
            Color tenue = Color.FromArgb(156, 163, 175);

            using Form about = new Form();

            about.Text = "Acerca de VacunaPC";
            about.ClientSize = new Size(480, 320);
            about.StartPosition =
                FormStartPosition.CenterScreen;

            about.FormBorderStyle =
                FormBorderStyle.FixedDialog;

            about.MaximizeBox = false;
            about.MinimizeBox = false;
            about.ShowInTaskbar = false;
            about.BackColor = fondo;
            about.Icon = SystemIcons.Shield;

            Label title = new Label()
            {
                Text = "VacunaPC",
                Font = new Font("Segoe UI", 26, FontStyle.Bold),
                ForeColor = acento,
                Location = new Point(28, 20),
                AutoSize = true
            };

            Label version = new Label()
            {
                Text = "Versión 2.0",
                Font = new Font("Segoe UI", 10),
                ForeColor = tenue,
                Location = new Point(32, 76),
                AutoSize = true
            };

            Label description = new Label()
            {
                Text = "Herramienta de protección y remediación de " +
                       "memorias USB.\nDetecta y aísla ejecutables que se " +
                       "hacen pasar por carpetas y restaura los archivos ocultos.",
                Font = new Font("Segoe UI", 10),
                ForeColor = texto,
                Location = new Point(32, 106),
                Size = new Size(416, 56)
            };

            Panel linea = new Panel()
            {
                BackColor = panel,
                Location = new Point(32, 172),
                Size = new Size(416, 2)
            };

            Label desarrollado = new Label()
            {
                Text = "Desarrollado por",
                Font = new Font("Segoe UI", 9),
                ForeColor = tenue,
                Location = new Point(32, 186),
                AutoSize = true
            };

            Label author = new Label()
            {
                Text = AUTOR,
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                ForeColor = texto,
                Location = new Point(30, 206),
                AutoSize = true
            };

            Label institution = new Label()
            {
                Text = "Sistemas UNEZ · Universidad Emiliano Zapata\n" +
                       "Departamento de Sistemas / TI",
                Font = new Font("Segoe UI", 10),
                ForeColor = tenue,
                Location = new Point(32, 242),
                AutoSize = true
            };

            Button close = new Button()
            {
                Text = "Cerrar",
                Location = new Point(368, 266),
                Size = new Size(85, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = acento,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                DialogResult = DialogResult.OK
            };

            close.FlatAppearance.BorderSize = 0;

            about.Controls.AddRange(new Control[]
            {
                title,
                version,
                description,
                linea,
                desarrollado,
                author,
                institution,
                close
            });

            about.AcceptButton = close;
            about.ShowDialog(this);
        }

        private void OpenFolder(string path)
        {
            Directory.CreateDirectory(path);

            Process.Start(new ProcessStartInfo()
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }

        private void OpenFile(string path)
        {
            if (!File.Exists(path))
            {
                MessageBox.Show("Todavía no existe el registro.");
                return;
            }

            Process.Start(new ProcessStartInfo()
            {
                FileName = "notepad.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }

        private void ExitApplication()
        {
            allowExit = true;
            Close();
        }

        protected override void OnFormClosing(
            FormClosingEventArgs e)
        {
            if (!allowExit &&
                e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(
            FormClosedEventArgs e)
        {
            timerUnidades.Stop();
            timerUnidades.Dispose();

            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayMenu.Dispose();
            scanLock.Dispose();

            Log("VacunaPC finalizada.");

            base.OnFormClosed(e);
        }

        private void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(basePath);

                File.AppendAllText(
                    LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] " +
                    message + Environment.NewLine);
            }
            catch { }
        }
    }
}