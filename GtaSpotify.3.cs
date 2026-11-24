// GTA Spotify Radio - v1.0.0
// Script para Script Hook V .NET (Enhanced). Se compila solo al iniciar el juego (o con Insert).
// IMPORTANTE: el compilador interno solo soporta C# 5 y NO referencia DLLs de la carpeta scripts,
// por eso el menu es propio y iFruitAddon2 se carga por reflexion.
//
// Opcional en la carpeta scripts: iFruitAddon2.dll (contacto "Spotify" en el telefono).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GTA;
using GTA.Native;

public class GtaSpotify : Script
{
    // ===================== CONFIGURACION =====================
    const int Port = 47823;
    const Keys KeyMenu = Keys.F6;      // abrir/cerrar el menu de Spotify
    const Keys KeyToggle = Keys.F7;    // activar/desactivar modo Spotify
    const Keys KeyPrev = Keys.F8;      // tema anterior
    const Keys KeyNext = Keys.F9;      // tema siguiente
    const Keys KeyVolDown = Keys.F10;  // bajar volumen de Spotify
    const Keys KeyVolUp = Keys.F11;    // subir volumen de Spotify
    const string VolumeStep = "0.1";   // 10% por pulsacion de tecla
    const int MenuVolumeStep = 5;      // % por pulsacion en el menu

    const bool DuckEnabled = true;     // bajar volumen en dialogos de historia/misiones/eventos y llamadas
    const string DuckLevel = "0.25";   // volumen durante el dialogo (25% del normal)
    const bool DuckInCutscenes = true; // tambien durante cinematicas
    const int DuckHoldMs = 2500;       // espera antes de volver a subir (evita subir y bajar entre frases)

    const bool RadioButtonsControlSpotify = true; // con el modo activo, los botones de emisora controlan Spotify
    const int LongPressMs = 700;       // mantener el boton de emisora este tiempo desactiva Spotify
    // Emisora personalizada: reemplaza una emisora del juego por un MP3 que suena "en vivo".
    // Archivos en Documentos\gta-spotify\radios\: <CustomRadioFile>.mp3, .txt (lista de canciones) y .png (logo)
    const bool CustomRadioEnabled = false;                // emisora personalizada opcional (ver README)
    const string CustomRadioStation = "RADIO_08_MEXICAN"; // emisora que se reemplaza (East Los FM)
    const string CustomRadioName = "Mi Radio FM";
    const string CustomRadioFile = "MiRadio";
    const int CustomRadioVolume = 55;                     // volumen base 0-100 (se ajusta con F10/F11 sintonizada y se guarda)
    const bool CustomRadioFollowMusicVolume = true;       // escala con "Volumen de la música" de los ajustes de audio del juego
    const bool CustomRadioWheelText = true;               // muestra el tema actual en la rueda de radio
    // true  = el mod reproduce el MP3 y silencia la emisora original (no hace falta tocar archivos)
    // false = el audio ya esta reemplazado en mods\ (RADIO_08_MEXICAN.rpf): el mod solo muestra los nombres
    const bool CustomRadioUsePlayer = true;
    const bool CustomRadioToast = false;                  // aviso propio con logo y tema (desactivado: se usa la rueda del juego)
    const int CustomRadioTrackIdBase = 8100;              // trackid del primer elemento de la lista (marcadores del .awc)

    const int ListRows = 10;           // filas visibles en las listas de playlists/albumes
    const int ToastMs = 6000;          // duracion del aviso de tema nuevo
    const int EnterGraceMs = 2500;     // tiempo para forzar radio apagada al subir al auto
    const int LogicIntervalMs = 150;
    // =========================================================

    static readonly Color Green = Color.FromArgb(255, 29, 185, 84);
    static readonly Color PanelColor = Color.FromArgb(215, 12, 12, 12);
    static readonly Color Grey = Color.FromArgb(255, 170, 170, 170);
    static readonly Color Selected = Color.FromArgb(240, 235, 235, 235);
    static readonly Color Black = Color.FromArgb(240, 0, 0, 0);

    // Red
    HttpListener listener;
    volatile WebSocket socket;
    readonly ConcurrentQueue<string> incoming = new ConcurrentQueue<string>();
    readonly object sendLock = new object();
    readonly CancellationTokenSource cts = new CancellationTokenSource();

    // Estado de reproduccion
    bool spotifyMode;
    bool lastShouldPlay;
    bool wasInVehicle;
    bool connected;
    bool isPlaying;
    bool shuffle;
    int repeatMode; // 0 no, 1 contexto, 2 tema
    int graceUntil;
    int nextLogicAt;
    bool ducked;
    int lastTalkAt = -100000;
    string trackTitle = "";
    string trackArtist = "";
    string trackUri = "";
    int volumePct = -1;
    RadioStation prevStation = RadioStation.RadioOff; // emisora que sonaba antes de activar Spotify

    // Botones de emisora (control 81 = siguiente emisora, 82 = anterior, 85 = rueda de radio)
    const int CtrlNextRadio = 81, CtrlPrevRadio = 82, CtrlRadioWheel = 85;
    int radioBtn;
    int radioBtnDownAt;
    bool radioLongFired;

    // Portadas
    string coverDir;
    string session;
    int coverCounter;
    int trackCoverRequest;
    GTA.UI.CustomSprite coverSprite;
    readonly Dictionary<string, GTA.UI.CustomSprite> listCovers = new Dictionary<string, GTA.UI.CustomSprite>();
    readonly HashSet<string> listCoversPending = new HashSet<string>();
    int toastUntil;

    // Menu
    bool menuOpen;
    int menuIndex;
    int menuOpenedAt;
    static readonly string[] MenuItems =
        { "Modo Spotify", "Play / Pausa", "Siguiente", "Anterior", "Aleatorio", "Repetir", "Playlists", "Álbumes", "Volumen" };
    const int ItShuffle = 4, ItRepeat = 5, ItPlaylists = 6, ItAlbums = 7, ItVolume = 8;

    // Listas (playlists / albumes)
    class LibList
    {
        public string[] Names = new string[0];
        public string[] Uris = new string[0];
        public string[] Images = new string[0];
        public string[] Subs = new string[0];
        public bool Loading;
    }
    readonly LibList playlists = new LibList();
    readonly LibList albums = new LibList();
    LibList activeList;   // null = menu principal
    string activeTitle = "";
    int listIndex;
    int listScroll;

    // Emisora personalizada
    RadioPlayer radio;
    string radioDir;
    double[] crStarts = new double[0];
    string[] crArtists = new string[0];
    string[] crTitles = new string[0];
    double crDuration;
    double crOffset;       // segundos sumados al reloj "en vivo" al saltar temas
    double crClock = -1;   // reloj de la emisora: avanza solo mientras el juego no esta en pausa
    DateTime crClockLast;
    int crBaseVolume = CustomRadioVolume;
    int crMusicSetting = 10;
    int crNextSettingCheck;
    int crWheelGameId = -1;
    int crWheelTrack = -2;
    int crTrack = -1;
    bool crActive;
    bool frontendMuted;
    int crVolumeSent = -1;
    int crToastUntil;
    GTA.UI.CustomSprite crLogo;

    // Telefono (iFruitAddon2 por reflexion)
    object ifruit;
    MethodInfo ifruitUpdate;
    MethodInfo ifruitClose;
    bool pendingOpenMenu;

    string logPath;

    public GtaSpotify()
    {
        // El juego puede crear archivos en Program Files pero no modificar ni borrar los existentes,
        // asi que el log va a Documentos y las portadas a la carpeta temporal de Windows.
        session = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string logDir = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "gta-spotify"), "logs");
        try { Directory.CreateDirectory(logDir); } catch (Exception) { }
        logPath = Path.Combine(logDir, "GtaSpotify_" + session + ".log");
        coverDir = Path.Combine(Path.GetTempPath(), "GtaSpotify");
        Log("Iniciando v1.0.0");
        InitCustomRadio();
        PrepareCoverDir();
        BuildPhone();

        Tick += OnTick;
        KeyDown += OnKeyDown;
        Aborted += OnAborted;
        StartServer();
    }

    // ---------------- Inicializacion ----------------

    void PrepareCoverDir()
    {
        try
        {
            Directory.CreateDirectory(coverDir);
            // Las de sesiones anteriores pueden seguir bloqueadas por ScriptHookV: se borran las que se pueda.
            foreach (string f in Directory.GetFiles(coverDir, "*.png"))
            {
                try { File.Delete(f); } catch (Exception) { }
            }
        }
        catch (Exception ex) { Log("No se pudo preparar la carpeta de portadas: " + ex.Message); }
    }

    void BuildPhone()
    {
        try
        {
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "iFruitAddon2");
            if (asm == null)
            {
                string dll = Path.Combine(BaseDirectory, "iFruitAddon2.dll");
                if (!File.Exists(dll)) { Log("iFruitAddon2.dll no encontrado: sin contacto en el telefono (usa F6)"); return; }
                asm = Assembly.LoadFrom(dll);
            }

            Type tFruit = asm.GetType("iFruitAddon2.CustomiFruit", true);
            Type tContact = asm.GetType("iFruitAddon2.iFruitContact", true);

            object fruit = Activator.CreateInstance(tFruit);
            object contact = Activator.CreateInstance(tContact, new object[] { "Spotify" });
            SetProp(contact, "DialTimeout", 800);
            SetProp(contact, "Active", true);

            EventInfo ev = tContact.GetEvent("Answered");
            MethodInfo handler = typeof(GtaSpotify).GetMethod("OnContactAnswered", BindingFlags.Instance | BindingFlags.Public);
            ev.AddEventHandler(contact, Delegate.CreateDelegate(ev.EventHandlerType, this, handler));

            object contacts = tFruit.GetProperty("Contacts").GetValue(fruit, null);
            MethodInfo add = contacts.GetType().GetMethods()
                .First(m => m.Name == "Add" && m.GetParameters().Length == 1);
            add.Invoke(contacts, new object[] { contact });

            ifruitUpdate = tFruit.GetMethod("Update", Type.EmptyTypes);
            ifruitClose = tFruit.GetMethods().FirstOrDefault(m => m.Name == "Close" && m.GetParameters().Length == 0)
                       ?? tFruit.GetMethods().FirstOrDefault(m => m.Name == "Close" && m.GetParameters().Length == 1);
            ifruit = fruit;
            Log("Contacto Spotify agregado al telefono");
        }
        catch (Exception ex)
        {
            ifruit = null;
            Log("No se pudo agregar el contacto (iFruitAddon2): " + ex);
        }
    }

    static void SetProp(object obj, string name, object value)
    {
        PropertyInfo p = obj.GetType().GetProperty(name);
        if (p != null && p.CanWrite) p.SetValue(obj, value, null);
    }

    // Llamado por iFruitAddon2 al "atender" el contacto
    public void OnContactAnswered(object contact)
    {
        pendingOpenMenu = true;
    }

    void ClosePhone()
    {
        if (ifruit == null || ifruitClose == null) return;
        try
        {
            if (ifruitClose.GetParameters().Length == 0) ifruitClose.Invoke(ifruit, null);
            else ifruitClose.Invoke(ifruit, new object[] { 0 });
        }
        catch (Exception ex) { Log("Error cerrando el telefono: " + ex.Message); }
    }

    // ---------------- Servidor WebSocket ----------------

    void StartServer()
    {
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:" + Port + "/");
            listener.Start();
            Log("Servidor escuchando en ws://localhost:" + Port + "/");
            Task.Run(() => AcceptLoop());
        }
        catch (Exception ex)
        {
            Log("No se pudo iniciar el servidor: " + ex);
            incoming.Enqueue("_error\tNo se pudo abrir el puerto " + Port + ". Ver GtaSpotify.log");
        }
    }

    async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (Exception)
            {
                if (cts.IsCancellationRequested) break;
                continue;
            }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }

            try
            {
                HttpListenerWebSocketContext wsCtx = await ctx.AcceptWebSocketAsync(null);
                WebSocket old = socket;
                socket = wsCtx.WebSocket;
                if (old != null) { try { old.Abort(); } catch (Exception) { } }
                incoming.Enqueue("_connected");
                Task ignored = ReceiveLoop(wsCtx.WebSocket);
            }
            catch (Exception ex)
            {
                Log("Error aceptando WebSocket: " + ex.Message);
            }
        }
    }

    async Task ReceiveLoop(WebSocket ws)
    {
        byte[] buffer = new byte[16384];
        List<byte> acc = new List<byte>();
        try
        {
            while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                WebSocketReceiveResult r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                for (int i = 0; i < r.Count; i++) acc.Add(buffer[i]);
                if (r.EndOfMessage)
                {
                    incoming.Enqueue(Encoding.UTF8.GetString(acc.ToArray()));
                    acc.Clear();
                }
            }
        }
        catch (Exception) { }
        if (socket == ws)
        {
            socket = null;
            incoming.Enqueue("_disconnected");
        }
    }

    void Send(string text)
    {
        WebSocket ws = socket;
        if (ws == null || ws.State != WebSocketState.Open) return;
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            lock (sendLock)
            {
                ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None).Wait(500);
            }
        }
        catch (Exception ex)
        {
            Log("Error enviando '" + text + "': " + ex.Message);
        }
    }

    // ---------------- Bucle principal ----------------

    void OnTick(object sender, EventArgs e)
    {
        if (radio != null) radio.Heartbeat = Environment.TickCount; // si deja de latir (menu de pausa), el reproductor se pausa solo
        string msg;
        while (incoming.TryDequeue(out msg)) HandleMessage(msg);

        if (ifruit != null && ifruitUpdate != null)
        {
            try { ifruitUpdate.Invoke(ifruit, null); }
            catch (Exception ex) { Log("iFruitAddon2 fallo, se desactiva: " + ex); ifruit = null; }
        }

        if (pendingOpenMenu)
        {
            pendingOpenMenu = false;
            ClosePhone();
            OpenMenu();
        }

        if (RadioButtonsControlSpotify) HandleRadioButtons();

        if (menuOpen)
        {
            ProcessMenuInput();
            if (menuOpen) DrawMenu();
        }
        else if (CustomRadioToast && Game.GameTime < crToastUntil)
        {
            DrawRadioToast();
        }
        else if (Game.GameTime < toastUntil)
        {
            DrawToast();
        }

        if (Game.GameTime >= nextLogicAt)
        {
            nextLogicAt = Game.GameTime + LogicIntervalMs;
            UpdateLogic();
        }
    }

    void UpdateLogic()
    {
        Ped player = Game.Player.Character;
        Vehicle veh = player.CurrentVehicle;
        bool inVehicle = veh != null && player.IsInVehicle();

        if (inVehicle && !wasInVehicle) graceUntil = Game.GameTime + EnterGraceMs;
        wasInVehicle = inVehicle;

        if (spotifyMode && inVehicle && Game.RadioStation != RadioStation.RadioOff)
        {
            if (Game.GameTime < graceUntil)
            {
                prevStation = Game.RadioStation;           // la recordamos para devolverla al desactivar
                Game.RadioStation = RadioStation.RadioOff; // el juego enciende la radio sola al subir
            }
            else
            {
                spotifyMode = false;
                Notify("~g~Spotify~s~: desactivado (elegiste una emisora)");
            }
        }

        bool shouldPlay = spotifyMode && inVehicle && veh.IsEngineRunning && !Game.IsPaused;
        if (shouldPlay != lastShouldPlay)
        {
            Send(shouldPlay ? "play" : "pause");
            lastShouldPlay = shouldPlay;
        }

        if (DuckEnabled) UpdateDucking();
        bool paused = Game.IsPaused || Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE);
        if (CustomRadioEnabled) UpdateCustomRadio(inVehicle && veh.IsEngineRunning && !paused, paused);
    }

    void UpdateDucking()
    {
        // Conversaciones "con guion": historia, misiones, eventos aleatorios y llamadas.
        // Las frases sueltas de peatones (ambient speech) no cuentan.
        bool talking =
            Function.Call<bool>(Hash.IS_SCRIPTED_CONVERSATION_ONGOING) ||
            Function.Call<bool>(Hash.IS_MOBILE_PHONE_CALL_ONGOING) ||
            (DuckInCutscenes && Function.Call<bool>(Hash.IS_CUTSCENE_PLAYING));

        if (talking) lastTalkAt = Game.GameTime;
        bool shouldDuck = Game.GameTime - lastTalkAt < DuckHoldMs;
        if (shouldDuck != ducked)
        {
            ducked = shouldDuck;
            Send(ducked ? "duck\t1\t" + DuckLevel : "duck\t0");
            Log(ducked ? "Bajando volumen (dialogo)" : "Restaurando volumen");
        }
    }

    // ---------------- Mensajes de Spicetify ----------------

    void HandleMessage(string msg)
    {
        int tab = msg.IndexOf('\t');
        string cmd = tab < 0 ? msg : msg.Substring(0, tab);
        string body = tab < 0 ? "" : msg.Substring(tab + 1);
        string[] p = msg.Split('\t');

        switch (cmd)
        {
            case "_connected":
                connected = true;
                ducked = false;
                Log("Spicetify conectado");
                Notify("~g~Spotify~s~: conectado");
                lastShouldPlay = false; // re-sincroniza en el proximo tick
                break;
            case "_disconnected":
                connected = false;
                Log("Spicetify desconectado");
                if (spotifyMode) Notify("~g~Spotify~s~: ~r~desconectado");
                break;
            case "_error":
                Notify("~g~Spotify~s~: ~r~" + body);
                break;
            case "_cover":
                // _cover \t clave \t ruta
                if (p.Length >= 3) LoadCover(p[1], p[2]);
                break;
            case "track":
                // track \t titulo \t artista \t urlPortada \t uri
                if (p.Length >= 3)
                {
                    string uri = p.Length >= 5 ? p[4] : p[1];
                    bool changed = uri != trackUri;
                    trackTitle = p[1];
                    trackArtist = p[2];
                    trackUri = uri;
                    if (changed)
                    {
                        coverSprite = null;
                        trackCoverRequest++;
                        if (p.Length >= 4 && p[3].StartsWith("http")) DownloadCover(p[3], "__track" + trackCoverRequest);
                        if (spotifyMode && Game.Player.Character.IsInVehicle()) toastUntil = Game.GameTime + ToastMs;
                    }
                }
                break;
            case "state":
                // state \t reproduciendo \t aleatorio \t repetir
                isPlaying = p.Length >= 2 && p[1] == "1";
                if (p.Length >= 3) shuffle = p[2] == "1";
                int rep;
                if (p.Length >= 4 && int.TryParse(p[3], out rep)) repeatMode = rep;
                break;
            case "volume":
                int v;
                if (p.Length >= 2 && int.TryParse(p[1], out v))
                {
                    bool fromKey = volumePct >= 0 && v != volumePct && !menuOpen;
                    volumePct = v;
                    if (fromKey) Notify("~g~Spotify~s~: volumen " + v + "%");
                }
                break;
            case "log":
                Log("[Spotify] " + body);
                break;
            case "playlists":
                FillList(playlists, body);
                break;
            case "albums":
                FillList(albums, body);
                break;
        }
    }

    // Formato: elementos separados por \u001e, campos por \u001f: nombre, uri, urlImagen, subtitulo
    void FillList(LibList list, string body)
    {
        string[] items = body.Length > 0 ? body.Split('\u001e') : new string[0];
        list.Names = new string[items.Length];
        list.Uris = new string[items.Length];
        list.Images = new string[items.Length];
        list.Subs = new string[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            string[] f = items[i].Split('\u001f');
            list.Names[i] = f[0];
            list.Uris[i] = f.Length > 1 ? f[1] : "";
            list.Images[i] = f.Length > 2 ? f[2] : "";
            list.Subs[i] = f.Length > 3 ? f[3] : "";
        }
        list.Loading = false;
        if (list == activeList)
        {
            listIndex = Math.Min(listIndex, Math.Max(0, items.Length - 1));
            RequestListCovers();
        }
        Log("Lista recibida: " + items.Length + " elementos");
    }

    // ---------------- Portadas ----------------

    void DownloadCover(string url, string key)
    {
        int n = Interlocked.Increment(ref coverCounter);
        // Nombre unico por sesion: ScriptHookV guarda las texturas por ruta y no las suelta al recargar.
        string path = Path.Combine(coverDir, "c_" + session + "_" + n + ".png");
        Task.Run(() =>
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                byte[] data;
                using (WebClient wc = new WebClient()) { data = wc.DownloadData(url); }
                using (MemoryStream ms = new MemoryStream(data))
                using (Image img = Image.FromStream(ms))
                using (Bitmap small = new Bitmap(img, new Size(256, 256)))
                {
                    small.Save(path, ImageFormat.Png);
                }
                incoming.Enqueue("_cover\t" + key + "\t" + path);
            }
            catch (Exception ex)
            {
                Log("Error descargando portada (" + url + "): " + ex.Message);
                incoming.Enqueue("_cover\t" + key + "\t");
            }
        });
    }

    void LoadCover(string key, string path)
    {
        GTA.UI.CustomSprite sprite = null;
        if (path.Length > 0 && File.Exists(path))
        {
            try { sprite = new GTA.UI.CustomSprite(path, new SizeF(64, 64), PointF.Empty); }
            catch (Exception ex) { Log("No se pudo cargar la portada: " + ex.Message); }
        }

        if (key.StartsWith("__track"))
        {
            if (key == "__track" + trackCoverRequest) coverSprite = sprite;
        }
        else
        {
            listCoversPending.Remove(key);
            if (sprite != null) listCovers[key] = sprite;
        }
    }

    // Descarga la portada del elemento elegido y de sus vecinos
    void RequestListCovers()
    {
        if (activeList == null) return;
        for (int i = listIndex - 1; i <= listIndex + 2; i++)
        {
            if (i < 0 || i >= activeList.Uris.Length) continue;
            string uri = activeList.Uris[i];
            string img = activeList.Images[i];
            if (uri.Length == 0 || !img.StartsWith("http")) continue;
            if (listCovers.ContainsKey(uri) || listCoversPending.Contains(uri)) continue;
            listCoversPending.Add(uri);
            DownloadCover(img, uri);
        }
    }

    // ---------------- Menu ----------------

    void OpenMenu()
    {
        menuOpen = true;
        menuIndex = 0;
        activeList = null;
        menuOpenedAt = Game.GameTime;
        Sound("SELECT");
    }

    void CloseMenu()
    {
        menuOpen = false;
        activeList = null;
        Sound("BACK");
    }

    void OpenList(LibList list, string title, string request)
    {
        activeList = list;
        activeTitle = title;
        listIndex = 0;
        listScroll = 0;
        list.Loading = true;
        Send(request);
        RequestListCovers();
    }

    // Controles que siguen funcionando con el menu abierto: camara, caminar, acelerar, frenar, girar y freno de mano.
    // Ninguno usa las flechas, Enter ni Retroceso.
    static readonly int[] AllowedControls = { 1, 2, 30, 31, 59, 71, 72, 76 };

    bool JustPressed(GTA.Control c)
    {
        return Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)c);
    }

    void Sound(string name)
    {
        Audio.PlaySoundFrontend(name, "HUD_FRONTEND_DEFAULT_SOUNDSET");
    }

    void ProcessMenuInput()
    {
        Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);
        foreach (int c in AllowedControls)
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, c, true);
        Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, (int)GTA.Control.FrontendPause, true);
        Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, (int)GTA.Control.FrontendPauseAlternate, true);

        // Evita que la misma pulsacion que abrio el menu lo active
        if (Game.GameTime - menuOpenedAt < 250) return;

        if (JustPressed(GTA.Control.PhoneCancel) || JustPressed(GTA.Control.FrontendPause) || JustPressed(GTA.Control.FrontendPauseAlternate))
        {
            if (activeList != null) { activeList = null; Sound("BACK"); }
            else CloseMenu();
            return;
        }

        if (activeList != null) { ProcessListInput(); return; }

        if (JustPressed(GTA.Control.PhoneUp))
        {
            menuIndex = (menuIndex + MenuItems.Length - 1) % MenuItems.Length;
            Sound("NAV_UP_DOWN");
        }
        if (JustPressed(GTA.Control.PhoneDown))
        {
            menuIndex = (menuIndex + 1) % MenuItems.Length;
            Sound("NAV_UP_DOWN");
        }
        if (menuIndex == ItVolume)
        {
            int delta = 0;
            if (JustPressed(GTA.Control.PhoneLeft)) delta = -MenuVolumeStep;
            if (JustPressed(GTA.Control.PhoneRight)) delta = MenuVolumeStep;
            if (delta != 0)
            {
                int v = Math.Max(0, Math.Min(100, (volumePct < 0 ? 50 : volumePct) + delta));
                volumePct = v;
                Send("setvol\t" + (v / 100.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
                Sound("NAV_LEFT_RIGHT");
            }
        }
        if (JustPressed(GTA.Control.PhoneSelect))
        {
            Sound("SELECT");
            switch (menuIndex)
            {
                case 0: SetMode(!spotifyMode); break;
                case 1: Send("toggle"); break;
                case 2: Send("next"); break;
                case 3: Send("prev"); break;
                case ItShuffle: shuffle = !shuffle; Send("shuffle\t" + (shuffle ? "1" : "0")); break;
                case ItRepeat: repeatMode = (repeatMode + 1) % 3; Send("repeat\t" + repeatMode); break;
                case ItPlaylists: OpenList(playlists, "PLAYLISTS", "getplaylists"); break;
                case ItAlbums: OpenList(albums, "ÁLBUMES GUARDADOS", "getalbums"); break;
            }
        }
    }

    void ProcessListInput()
    {
        int n = activeList.Names.Length;
        if (n == 0) return;
        int before = listIndex;
        if (JustPressed(GTA.Control.PhoneUp)) listIndex = (listIndex + n - 1) % n;
        if (JustPressed(GTA.Control.PhoneDown)) listIndex = (listIndex + 1) % n;
        if (JustPressed(GTA.Control.PhoneLeft)) listIndex = Math.Max(0, listIndex - ListRows);
        if (JustPressed(GTA.Control.PhoneRight)) listIndex = Math.Min(n - 1, listIndex + ListRows);
        if (listIndex != before)
        {
            Sound("NAV_UP_DOWN");
            RequestListCovers();
        }
        if (listIndex < listScroll) listScroll = listIndex;
        if (listIndex >= listScroll + ListRows) listScroll = listIndex - ListRows + 1;

        if (JustPressed(GTA.Control.PhoneSelect) && activeList.Uris[listIndex].Length > 0)
        {
            Sound("SELECT");
            // Si ahora no deberia sonar (fuera del auto, etc.) la extension la carga y la pausa
            Send("playuri\t" + activeList.Uris[listIndex] + "\t" + (lastShouldPlay ? "1" : "0"));
            Notify("~g~Spotify~s~: " + activeList.Names[listIndex]);
            activeList = null;
        }
    }

    string MenuValue(int i)
    {
        switch (i)
        {
            case 0: return spotifyMode ? "Activado" : "Desactivado";
            case 1: return isPlaying ? "Reproduciendo" : "Pausado";
            case ItShuffle: return shuffle ? "Sí" : "No";
            case ItRepeat: return repeatMode == 1 ? "Lista" : repeatMode == 2 ? "Tema" : "No";
            case ItPlaylists: return ">";
            case ItAlbums: return ">";
            case ItVolume: return "< " + (volumePct < 0 ? "?" : volumePct.ToString()) + "% >";
        }
        return "";
    }

    void DrawMenu()
    {
        // Coordenadas en espacio 1280x720
        const float x = 24f, y = 24f, w = 300f;
        const float headerH = 54f, subH = 26f, infoH = 50f, rowH = 30f;
        float cy = y;

        DrawRect(x, cy, w, headerH, Green);
        DrawText("Spotify", x + 14, cy + 6, 0.95f, Color.White, GTA.UI.Font.HouseScript);
        cy += headerH;

        DrawRect(x, cy, w, subH, Black);
        DrawText(connected ? "CONECTADO" : "SIN CONEXION CON SPOTIFY", x + 10, cy + 4, 0.32f, connected ? Green : Color.FromArgb(255, 224, 50, 50), GTA.UI.Font.ChaletLondon);
        cy += subH;

        DrawRect(x, cy, w, infoH, PanelColor);
        DrawText(trackTitle.Length > 0 ? Trim(trackTitle, 30) : "Sin reproduccion", x + 10, cy + 4, 0.4f, Color.White, GTA.UI.Font.ChaletLondon);
        DrawText(Trim(trackArtist, 34), x + 10, cy + 26, 0.34f, Grey, GTA.UI.Font.ChaletLondon);
        cy += infoH;

        GTA.UI.CustomSprite sideCover = coverSprite;
        string sideTitle = trackTitle;
        string sideSub = trackArtist;

        if (activeList != null)
        {
            LibList l = activeList;
            DrawRect(x, cy, w, 24f, Black);
            DrawText(activeTitle + (l.Names.Length > 0 ? "  (" + (listIndex + 1) + "/" + l.Names.Length + ")" : ""), x + 10, cy + 4, 0.3f, Green, GTA.UI.Font.ChaletLondon);
            cy += 24f;
            if (l.Names.Length == 0)
            {
                DrawRect(x, cy, w, rowH, PanelColor);
                DrawText(l.Loading ? "Cargando..." : "No se encontró nada", x + 10, cy + 4, 0.36f, Grey, GTA.UI.Font.ChaletLondon);
                cy += rowH;
            }
            for (int i = listScroll; i < l.Names.Length && i < listScroll + ListRows; i++)
            {
                bool sel = i == listIndex;
                DrawRect(x, cy, w, rowH, sel ? Selected : PanelColor);
                DrawText(Trim(l.Names[i], 34), x + 10, cy + 4, 0.36f, sel ? Color.Black : Color.White, GTA.UI.Font.ChaletLondon);
                cy += rowH;
            }
            if (l.Names.Length > 0)
            {
                GTA.UI.CustomSprite s;
                sideCover = listCovers.TryGetValue(l.Uris[listIndex], out s) ? s : null;
                sideTitle = l.Names[listIndex];
                sideSub = l.Subs[listIndex];
            }
        }
        else
        {
            for (int i = 0; i < MenuItems.Length; i++)
            {
                bool sel = i == menuIndex;
                DrawRect(x, cy, w, rowH, sel ? Selected : PanelColor);
                Color tc = sel ? Color.Black : Color.White;
                DrawText(MenuItems[i], x + 10, cy + 4, 0.36f, tc, GTA.UI.Font.ChaletLondon);
                string val = MenuValue(i);
                if (val.Length > 0) DrawTextRight(val, x + w - 10, cy + 4, 0.36f, tc);
                cy += rowH;
            }
        }

        DrawRect(x, cy, w, 24f, Black);
        DrawText(activeList != null ? "Flechas: mover   Enter: reproducir   Retroceso: volver" : "Flechas: mover   Enter: elegir   Retroceso: cerrar",
                 x + 8, cy + 4, 0.26f, Grey, GTA.UI.Font.ChaletLondon);

        // Panel derecho: portada del tema actual o del elemento elegido en la lista
        const float cx = x + w + 10f, size = 180f;
        DrawRect(cx, y, size + 8, size + 56, PanelColor);
        if (sideCover != null)
        {
            sideCover.Size = new SizeF(size, size);
            sideCover.Position = new PointF(cx + 4, y + 4);
            sideCover.Draw();
        }
        else
        {
            DrawText("Sin portada", cx + 50, y + 80, 0.35f, Grey, GTA.UI.Font.ChaletLondon);
        }
        DrawText(Trim(sideTitle, 24), cx + 6, y + size + 10, 0.36f, Color.White, GTA.UI.Font.ChaletLondon);
        DrawText(Trim(sideSub, 28), cx + 6, y + size + 30, 0.3f, Grey, GTA.UI.Font.ChaletLondon);
    }

    void DrawToast()
    {
        const float w = 300f, h = 76f;
        float x = 1280f - w - 16f, y = 16f;
        DrawRect(x, y, w, h, PanelColor);
        DrawRect(x, y, 4, h, Green);
        float tx = x + 12f;
        if (coverSprite != null)
        {
            coverSprite.Size = new SizeF(64, 64);
            coverSprite.Position = new PointF(x + 10, y + 6);
            coverSprite.Draw();
            tx = x + 84f;
        }
        DrawText("SPOTIFY", tx, y + 6, 0.3f, Green, GTA.UI.Font.ChaletLondon);
        DrawText(Trim(trackTitle, 24), tx, y + 24, 0.4f, Color.White, GTA.UI.Font.ChaletLondon);
        DrawText(Trim(trackArtist, 28), tx, y + 46, 0.34f, Grey, GTA.UI.Font.ChaletLondon);
    }

    void DrawRect(float x, float y, float w, float h, Color c)
    {
        new GTA.UI.ContainerElement(new PointF(x, y), new SizeF(w, h), c).Draw();
    }

    void DrawText(string text, float x, float y, float scale, Color c, GTA.UI.Font font)
    {
        new GTA.UI.TextElement(text, new PointF(x, y), scale, c, font, GTA.UI.Alignment.Left, false, false).Draw();
    }

    void DrawTextRight(string text, float right, float y, float scale, Color c)
    {
        new GTA.UI.TextElement(text, new PointF(right, y), scale, c, GTA.UI.Font.ChaletLondon, GTA.UI.Alignment.Right, false, false).Draw();
    }

    static string Trim(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max - 1) + "...";
    }

    // ---------------- Controles ----------------

    void SetMode(bool on)
    {
        spotifyMode = on;
        if (on)
        {
            if (Game.Player.Character.IsInVehicle())
            {
                RadioStation current = Game.RadioStation;
                if (current != RadioStation.RadioOff) prevStation = current;
                Game.RadioStation = RadioStation.RadioOff;
            }
            graceUntil = Game.GameTime + EnterGraceMs;
        }
    }

    // Con el modo Spotify activo y dentro de un vehiculo:
    //  - toque corto en "siguiente emisora" = siguiente tema; en "emisora anterior" = tema anterior
    //  - mantener cualquiera de los dos = desactiva Spotify y vuelve la radio del juego
    void HandleRadioButtons()
    {
        if (!spotifyMode || menuOpen || !Game.Player.Character.IsInVehicle())
        {
            radioBtn = 0;
            return;
        }

        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, CtrlNextRadio, true);
        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, CtrlPrevRadio, true);
        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, CtrlRadioWheel, true);

        if (radioBtn == 0)
        {
            // Flecha izquierda / cruceta izquierda = "rueda de radio" (85): toque = siguiente, mantener = rueda
            if (Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, CtrlRadioWheel)) radioBtn = CtrlRadioWheel;
            else if (Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, CtrlNextRadio)) radioBtn = CtrlNextRadio;
            else if (Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, CtrlPrevRadio)) radioBtn = CtrlPrevRadio;
            if (radioBtn != 0) Log("Boton de radio presionado: control " + radioBtn);
            if (radioBtn != 0)
            {
                radioBtnDownAt = Game.GameTime;
                radioLongFired = false;
            }
            return;
        }

        bool down = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, radioBtn);
        if (down)
        {
            if (!radioLongFired && Game.GameTime - radioBtnDownAt >= LongPressMs)
            {
                radioLongFired = true;
                SetMode(false);
                Game.RadioStation = prevStation != RadioStation.RadioOff ? prevStation : (RadioStation)0;
                Sound("BACK");
                Notify("~g~Spotify~s~: desactivado, vuelve la radio");
            }
        }
        else
        {
            if (!radioLongFired) Send(radioBtn == CtrlPrevRadio ? "prev" : "next");
            radioBtn = 0;
        }
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == KeyMenu)
        {
            if (menuOpen) CloseMenu(); else OpenMenu();
        }
        else if (e.KeyCode == KeyToggle)
        {
            SetMode(!spotifyMode);
            if (spotifyMode) Notify("~g~Spotify~s~: activado" + (connected ? "" : " ~r~(sin conexion con Spotify)"));
            else Notify("~g~Spotify~s~: desactivado");
        }
        else if (crActive && e.KeyCode == KeyVolUp) ChangeCustomVolume(5);
        else if (crActive && e.KeyCode == KeyVolDown) ChangeCustomVolume(-5);
        else if (crActive && e.KeyCode == KeyNext) SkipCustomTrack(1);
        else if (crActive && e.KeyCode == KeyPrev) SkipCustomTrack(-1);
        else if (!spotifyMode) return;
        else if (e.KeyCode == KeyNext) Send("next");
        else if (e.KeyCode == KeyPrev) Send("prev");
        else if (e.KeyCode == KeyVolUp) Send("vol\t" + VolumeStep);
        else if (e.KeyCode == KeyVolDown) Send("vol\t-" + VolumeStep);
    }

    void OnAborted(object sender, EventArgs e)
    {
        if (ducked) Send("duck\t0");
        if (lastShouldPlay) Send("pause");
        if (frontendMuted) Function.Call(Hash.SET_FRONTEND_RADIO_ACTIVE, true);
        if (radio != null) radio.Shutdown();
        cts.Cancel();
        try { if (socket != null) socket.Abort(); } catch (Exception) { }
        try { if (listener != null) listener.Close(); } catch (Exception) { }
        Log("Script detenido");
    }

    // ---------------- Emisora personalizada ----------------

    void InitCustomRadio()
    {
        if (!CustomRadioEnabled) return;
        radioDir = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "gta-spotify"), "radios");
        string mp3 = Path.Combine(radioDir, CustomRadioFile + ".mp3");
        string txt = Path.Combine(radioDir, CustomRadioFile + ".txt");
        string png = Path.Combine(radioDir, CustomRadioFile + ".png");

        if (CustomRadioUsePlayer && !File.Exists(mp3)) { Log("Emisora personalizada: falta " + mp3); return; }
        LoadCustomVolume();

        try { LoadTracklist(txt); } catch (Exception ex) { Log("Error leyendo la lista de canciones: " + ex.Message); }
        if (File.Exists(png))
        {
            try { crLogo = new GTA.UI.CustomSprite(png, new SizeF(64, 64), PointF.Empty); }
            catch (Exception ex) { Log("No se pudo cargar el logo: " + ex.Message); }
        }

        // Cambia el nombre de la emisora en la rueda de radio
        try { Game.SetLocalizedString(Game.GenerateHash(CustomRadioStation), CustomRadioName); }
        catch (Exception ex) { Log("No se pudo renombrar la emisora: " + ex.Message); }

        if (!CustomRadioUsePlayer)
        {
            Log("Emisora personalizada en modo archivo: " + CustomRadioName + " (" + crStarts.Length + " canciones)");
            return;
        }
        radio = new RadioPlayer();
        radio.Logger = Log;
        radio.Start(mp3);
        Log("Emisora personalizada lista: " + CustomRadioName + " en lugar de " + CustomRadioStation + " (" + crStarts.Length + " canciones)");
    }

    void LoadTracklist(string path)
    {
        if (!File.Exists(path)) return;
        List<double> starts = new List<double>();
        List<string> artists = new List<string>();
        List<string> titles = new List<string>();
        foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            string[] f = line.Split('|');
            if (f.Length < 3) continue;
            double t = 0;
            foreach (string part in f[0].Trim().Split(':')) t = t * 60 + double.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
            starts.Add(t);
            artists.Add(f[1].Trim());
            titles.Add(f[2].Trim());
        }
        crStarts = starts.ToArray();
        crArtists = artists.ToArray();
        crTitles = titles.ToArray();
    }

    string CurrentStationName()
    {
        return Function.Call<string>(Hash.GET_PLAYER_RADIO_STATION_NAME) ?? "";
    }

    double LivePosition()
    {
        double dur = crDuration > 0 ? crDuration : radio.Duration;
        if (dur <= 0) return 0;
        if (crClock < 0) AdvanceClock(false);
        double now = crClock + crOffset;
        double pos = now % dur;
        return pos < 0 ? pos + dur : pos;
    }

    // Como las emisoras del juego: el tiempo corre mientras jugas y se congela en el menu de pausa
    void AdvanceClock(bool paused)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (crClock < 0)
        {
            crClock = (nowUtc - new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
        else if (!paused)
        {
            double dt = (nowUtc - crClockLast).TotalSeconds;
            if (dt > 0 && dt < 1) crClock += dt; // huecos de mas de 1 s (pausa, cargas, alt-tab) no cuentan
        }
        crClockLast = nowUtc;
    }

    int TrackAt(double pos)
    {
        int idx = -1;
        for (int i = 0; i < crStarts.Length; i++) if (crStarts[i] <= pos) idx = i;
        return idx;
    }

    void UpdateCustomRadio(bool canPlay, bool paused)
    {
        AdvanceClock(paused);
        if (!CustomRadioUsePlayer) { UpdateCustomRadioFileMode(canPlay); return; }
        if (radio == null) return;
        if (radio.Error != null)
        {
            Log("Emisora personalizada desactivada: " + radio.Error);
            Notify("~y~" + CustomRadioName + "~s~: ~r~no se pudo iniciar el reproductor");
            radio = null;
            return;
        }
        if (!radio.Ready) return;
        if (crDuration <= 0 && radio.Duration > 0) crDuration = radio.Duration;

        bool tuned = canPlay && !spotifyMode && CurrentStationName() == CustomRadioStation;

        if (tuned && !crActive)
        {
            crActive = true;
            Function.Call(Hash.SET_FRONTEND_RADIO_ACTIVE, false); // silencia la emisora original
            frontendMuted = true;
            crVolumeSent = -1;
            double pos = LivePosition();
            radio.Play(pos, CustomVolume());
            crTrack = TrackAt(pos);
            crToastUntil = Game.GameTime + ToastMs;
            Log("Sintonizada " + CustomRadioName + " en " + FormatTime(pos));
        }
        else if (!tuned && crActive)
        {
            crActive = false;
            radio.Pause();
            if (frontendMuted) { Function.Call(Hash.SET_FRONTEND_RADIO_ACTIVE, true); frontendMuted = false; }
        }

        if (!crActive) return;

        if (radio.AutoPaused)
        {
            radio.AutoPaused = false;
            radio.Play(LivePosition(), CustomVolume());
        }

        int vol = CustomVolume();
        if (vol != crVolumeSent) { radio.SetVolume(vol); crVolumeSent = vol; }
        UpdateWheelText();

        int track = TrackAt(LivePosition());
        if (track != crTrack)
        {
            crTrack = track;
            crToastUntil = Game.GameTime + ToastMs;
        }
    }

    // El audio lo reproduce el juego: leemos el trackid del marcador que esta sonando
    void UpdateCustomRadioFileMode(bool inCar)
    {
        bool tuned = inCar && !spotifyMode && CurrentStationName() == CustomRadioStation;
        if (!tuned) { crActive = false; crTrack = -1; return; }

        int id = Function.Call<int>(Hash.GET_AUDIBLE_MUSIC_TRACK_TEXT_ID);
        int idx = id - CustomRadioTrackIdBase;
        if (idx < 0 || idx >= crTitles.Length) idx = -1;

        if (!crActive)
        {
            crActive = true;
            crTrack = idx;
            crToastUntil = Game.GameTime + ToastMs;
            Log("Sintonizada " + CustomRadioName + " (trackid " + id + ")");
        }
        else if (idx != crTrack && idx >= 0)
        {
            crTrack = idx;
            crToastUntil = Game.GameTime + ToastMs;
        }
    }

    int CustomVolume()
    {
        if (CustomRadioFollowMusicVolume && Game.GameTime >= crNextSettingCheck)
        {
            crNextSettingCheck = Game.GameTime + 2000;
            int ms = Function.Call<int>(Hash.GET_PROFILE_SETTING, 301); // 301 = volumen de la musica (0-10)
            if (ms >= 0 && ms <= 10) crMusicSetting = ms;
        }
        double level = double.Parse(DuckLevel, System.Globalization.CultureInfo.InvariantCulture);
        double music = CustomRadioFollowMusicVolume ? crMusicSetting / 10.0 : 1.0;
        return (int)Math.Round(crBaseVolume * music * (ducked ? level : 1.0));
    }

    string VolumeFile()
    {
        return Path.Combine(radioDir, CustomRadioFile + ".volumen.txt");
    }

    void LoadCustomVolume()
    {
        try
        {
            int v;
            if (File.Exists(VolumeFile()) && int.TryParse(File.ReadAllText(VolumeFile()).Trim(), out v))
                crBaseVolume = Math.Max(0, Math.Min(100, v));
        }
        catch (Exception) { }
    }

    void ChangeCustomVolume(int delta)
    {
        crBaseVolume = Math.Max(0, Math.Min(100, crBaseVolume + delta));
        try { File.WriteAllText(VolumeFile(), crBaseVolume.ToString()); } catch (Exception) { }
        Notify("~y~" + CustomRadioName + "~s~: volumen " + crBaseVolume);
    }

    // Reemplaza el texto de la cancion que muestra la rueda (claves "<trackid>S" y "<trackid>A" de trackid.gxt2).
    // Necesita el trackid.gxt2 con espacio extra en mods\update\update2.rpf para que no se corten los nombres.
    void UpdateWheelText()
    {
        if (!CustomRadioWheelText || crTrack < 0) return;
        int gameId = Function.Call<int>(Hash.GET_AUDIBLE_MUSIC_TRACK_TEXT_ID);
        if (gameId <= 0) return;
        if (gameId == crWheelGameId && crTrack == crWheelTrack) return;
        crWheelGameId = gameId;
        crWheelTrack = crTrack;
        try
        {
            Game.SetLocalizedString(Game.GenerateHash(gameId + "S"), crTitles[crTrack]);
            Game.SetLocalizedString(Game.GenerateHash(gameId + "A"), crArtists[crTrack].ToUpper()); // el juego muestra los artistas en mayusculas
            Log("Rueda: trackid " + gameId + " -> " + crArtists[crTrack] + " - " + crTitles[crTrack]);
        }
        catch (Exception ex) { Log("No se pudo cambiar el texto de la rueda: " + ex.Message); }
    }

    void SkipCustomTrack(int dir)
    {
        if (!CustomRadioUsePlayer)
        {
            if (dir > 0) Function.Call(Hash.SKIP_RADIO_FORWARD); // el juego no permite volver atras
            return;
        }
        if (crStarts.Length == 0) return;
        double pos = LivePosition();
        int cur = TrackAt(pos);
        double target;
        if (dir > 0)
            target = cur + 1 < crStarts.Length ? crStarts[cur + 1] : (crDuration > 0 ? crDuration : 0) + crStarts[0];
        else
            target = (cur >= 0 && pos - crStarts[cur] > 5) ? crStarts[cur] : (cur > 0 ? crStarts[cur - 1] : crStarts[0]);
        crOffset += target - pos;
        radio.Play(LivePosition(), CustomVolume());
        crToastUntil = Game.GameTime + ToastMs;
    }

    static string FormatTime(double s)
    {
        TimeSpan t = TimeSpan.FromSeconds(s);
        return ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
    }

    void DrawRadioToast()
    {
        const float w = 300f, h = 76f;
        float x = 1280f - w - 16f, y = 16f;
        DrawRect(x, y, w, h, PanelColor);
        DrawRect(x, y, 4, h, Color.FromArgb(255, 255, 196, 0));
        float tx = x + 12f;
        if (crLogo != null)
        {
            crLogo.Size = new SizeF(64, 64);
            crLogo.Position = new PointF(x + 10, y + 6);
            crLogo.Draw();
            tx = x + 84f;
        }
        string title = crTrack >= 0 ? crTitles[crTrack] : "";
        string artist = crTrack >= 0 ? crArtists[crTrack] : "";
        DrawText(CustomRadioName.ToUpper(), tx, y + 6, 0.3f, Color.FromArgb(255, 255, 196, 0), GTA.UI.Font.ChaletLondon);
        DrawText(Trim(title, 24), tx, y + 24, 0.4f, Color.White, GTA.UI.Font.ChaletLondon);
        DrawText(Trim(artist, 28), tx, y + 46, 0.34f, Grey, GTA.UI.Font.ChaletLondon);
    }

    // Reproductor de MP3 con Windows Media Player, en su propio hilo STA (lo requiere el control COM)
    class RadioPlayer
    {
        public Action<string> Logger;
        public volatile bool Ready;
        public volatile string Error;
        double duration;
        public double Duration { get { return duration; } }

        System.Windows.Forms.Control invoker;
        object wmp, controls, settings;
        double pendingSeek = -1;
        bool wantPlaying;
        public volatile int Heartbeat = Environment.TickCount;
        public volatile bool AutoPaused;

        public void Start(string file)
        {
            Thread t = new Thread(() =>
            {
                try
                {
                    Type type = Type.GetTypeFromProgID("WMPlayer.OCX");
                    if (type == null) { Error = "Windows Media Player no esta instalado"; return; }
                    wmp = Activator.CreateInstance(type);
                    settings = Get(wmp, "settings");
                    Set(settings, "autoStart", false);
                    Set(wmp, "URL", file);
                    controls = Get(wmp, "controls");

                    invoker = new System.Windows.Forms.Control();
                    invoker.CreateControl();

                    System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
                    timer.Interval = 200;
                    timer.Tick += (s, e) => Poll();
                    timer.Start();

                    Ready = true;
                    System.Windows.Forms.Application.Run();
                }
                catch (Exception ex) { Error = ex.Message; }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        void Poll()
        {
            try
            {
                if (duration <= 0)
                {
                    object media = Get(wmp, "currentMedia");
                    if (media != null)
                    {
                        double d = Convert.ToDouble(Get(media, "duration"));
                        if (d > 0) { duration = d; Log("Duracion del MP3: " + d.ToString("0.0") + " s"); }
                    }
                }
                // El juego no ejecuta scripts en el menu de pausa: si el script deja de latir, pausamos
                if (wantPlaying && !AutoPaused && Environment.TickCount - Heartbeat > 400)
                {
                    Invoke(controls, "pause");
                    AutoPaused = true;
                }
                // playState 3 = reproduciendo: recien ahi se puede mover la posicion
                if (pendingSeek >= 0 && Convert.ToInt32(Get(wmp, "playState")) == 3)
                {
                    double target = pendingSeek;
                    if (duration > 0) target = target % duration;
                    Set(controls, "currentPosition", target);
                    pendingSeek = -1;
                }
            }
            catch (Exception ex) { Log("Error del reproductor: " + ex.Message); }
        }

        void Do(Action a)
        {
            if (!Ready || invoker == null) return;
            try { invoker.BeginInvoke(a); } catch (Exception ex) { Log("Error del reproductor: " + ex.Message); }
        }

        public void Play(double position, int volume)
        {
            Do(() =>
            {
                Set(settings, "volume", volume);
                pendingSeek = position;
                wantPlaying = true;
                AutoPaused = false;
                Invoke(controls, "play");
            });
        }

        public void Pause() { Do(() => { wantPlaying = false; AutoPaused = false; Invoke(controls, "pause"); }); }

        public void SetVolume(int volume) { Do(() => Set(settings, "volume", volume)); }

        public void Shutdown()
        {
            Do(() =>
            {
                try { Invoke(controls, "stop"); } catch (Exception) { }
                System.Windows.Forms.Application.ExitThread();
            });
        }

        void Log(string text) { if (Logger != null) Logger(text); }

        static object Get(object o, string name) { return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null); }
        static void Set(object o, string name, object value) { o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new object[] { value }); }
        static void Invoke(object o, string name) { o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, null); }
    }

    // ---------------- Utilidades ----------------

    void Notify(string text)
    {
        GTA.UI.Notification.Show(text);
    }

    void Log(string text)
    {
        try
        {
            using (FileStream fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (StreamWriter sw = new StreamWriter(fs))
            {
                sw.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text);
            }
        }
        catch (Exception) { }
    }
}
