// gta-radio.js - Extension de Spicetify para GTA Spotify Radio v0.8
// Se conecta al script de GTA (ws://localhost:47823/) y obedece sus comandos.
// Protocolo de texto separado por tabs:
//   GTA -> Spotify: play | pause | toggle | next | prev
//                   vol\t<delta 0..1> | setvol\t<0..1>
//                   duck\t1\t<nivel 0..1> | duck\t0
//                   getplaylists | getalbums | playuri\t<uri>\t<autoplay 1|0>
//                   shuffle\t<1|0> | repeat\t<0|1|2>
//   Spotify -> GTA: hello | track\t<titulo>\t<artista>\t<urlPortada>\t<uri>
//                   state\t<reproduciendo>\t<aleatorio>\t<repetir> | volume\t<0..100>
//                   playlists|albums\t<nombre US uri US imagen US subtitulo RS ...>

(function gtaRadio() {
  if (!(window.Spicetify && Spicetify.Player && Spicetify.Player.addEventListener)) {
    setTimeout(gtaRadio, 500);
    return;
  }

  const URL = "ws://localhost:47823/";
  const RETRY_MS = 3000;
  const DUCK_IN_MS = 400;
  const DUCK_OUT_MS = 900;

  let ws = null;
  let ducked = false;
  let duckLevel = 0.25;
  let baseVolume = null;    // volumen "normal" mientras esta bajado
  let restoreTarget = null; // volumen al que se esta volviendo (fade de subida en curso)
  let fadeTimer = null;

  const clamp = (v) => Math.min(1, Math.max(0, v));

  function send(text) {
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(text);
  }

  function log(text) {
    console.log("[gta-radio] " + text);
    send("log\t" + text);
  }

  const pct = (v) => Math.round((v || 0) * 100) + "%";

  function toHttp(raw) {
    raw = String(raw || "");
    if (raw.startsWith("spotify:image:")) return "https://i.scdn.co/image/" + raw.slice("spotify:image:".length);
    return raw.startsWith("http") ? raw : "";
  }

  function coverUrl(md) {
    return toHttp(md.image_url || md.image_large_url || md.image_small_url || "");
  }

  // Elige una imagen mediana (~300px) de una lista de imagenes de Spotify
  function pickImage(images) {
    if (!images || !images.length) return "";
    const withSize = images.filter((i) => i && (i.width || i.height));
    if (withSize.length) {
      withSize.sort((a, b) => Math.abs((a.width || a.height) - 300) - Math.abs((b.width || b.height) - 300));
      return toHttp(withSize[0].url);
    }
    return toHttp((images[0] && (images[0].url || images[0])) || "");
  }

  function sendTrack() {
    const data = Spicetify.Player.data || {};
    const item = data.item || data.track;
    if (!item) return;
    const md = item.metadata || {};
    const title = md.title || item.name || "";
    const artist = md.artist_name || (item.artists && item.artists[0] && item.artists[0].name) || "";
    send(["track", title, artist, coverUrl(md), item.uri || ""].join("\t"));
  }

  function normalVolume() {
    if (ducked && baseVolume !== null) return baseVolume;
    if (restoreTarget !== null) return restoreTarget;
    return Spicetify.Player.getVolume();
  }

  function sendVolume() {
    send("volume\t" + Math.round(normalVolume() * 100));
  }

  function sendState() {
    let shuffle = false, repeat = 0;
    try { shuffle = !!Spicetify.Player.getShuffle(); } catch (e) {}
    try { repeat = Spicetify.Player.getRepeat() || 0; } catch (e) {}
    send("state\t" + (Spicetify.Player.isPlaying() ? "1" : "0") + "\t" + (shuffle ? "1" : "0") + "\t" + repeat);
  }

  let verifyTimer = null;

  // Spotify puede ignorar cambios de volumen muy seguidos: pasos de 120 ms y verificacion final
  function fadeTo(target, ms, onDone) {
    clearInterval(fadeTimer);
    clearTimeout(verifyTimer);
    target = clamp(target);
    const start = Spicetify.Player.getVolume();
    const steps = Math.max(1, Math.round(ms / 120));
    let i = 0;
    fadeTimer = setInterval(() => {
      i++;
      Spicetify.Player.setVolume(i >= steps ? target : clamp(start + (target - start) * (i / steps)));
      if (i >= steps) {
        clearInterval(fadeTimer);
        verifyVolume(target, 0, onDone);
      }
    }, 120);
  }

  function verifyVolume(target, attempt, onDone) {
    verifyTimer = setTimeout(() => {
      const now = Spicetify.Player.getVolume();
      if (Math.abs(now - target) > 0.015 && attempt < 5) {
        log("volumen " + pct(now) + " en vez de " + pct(target) + ", reintento " + (attempt + 1));
        Spicetify.Player.setVolume(target);
        verifyVolume(target, attempt + 1, onDone);
      } else if (onDone) {
        onDone();
      }
    }, 250);
  }

  function setNormalVolume(v) {
    v = clamp(v);
    if (ducked) {
      baseVolume = v;
      fadeTo(v * duckLevel, 100);
    } else {
      clearInterval(fadeTimer);
      restoreTarget = null;
      Spicetify.Player.setVolume(v);
    }
    send("volume\t" + Math.round(v * 100));
  }

  function duck(on, level) {
    if (on && !ducked) {
      if (level >= 0 && level <= 1) duckLevel = level;
      // Si todavia estaba subiendo, el volumen normal es el destino de esa subida, no el valor a mitad de camino
      baseVolume = restoreTarget !== null ? restoreTarget : Spicetify.Player.getVolume();
      restoreTarget = null;
      ducked = true;
      log("bajando: normal " + pct(baseVolume) + " -> " + pct(baseVolume * duckLevel));
      fadeTo(baseVolume * duckLevel, DUCK_IN_MS);
    } else if (!on && ducked) {
      ducked = false;
      if (baseVolume !== null) {
        log("subiendo: " + pct(Spicetify.Player.getVolume()) + " -> " + pct(baseVolume));
        restoreTarget = baseVolume;
        fadeTo(baseVolume, DUCK_OUT_MS, () => { log("volumen final " + pct(Spicetify.Player.getVolume())); restoreTarget = null; });
      }
      baseVolume = null;
    }
  }

  function collectPlaylists(items, out) {
    for (const it of items || []) {
      if (!it) continue;
      if (it.type === "playlist" && it.uri) {
        const owner = (it.owner && (it.owner.name || it.owner.displayName)) || "";
        out.push([it.name || "Sin nombre", it.uri, pickImage(it.images), owner ? "de " + owner : "Playlist"]);
      }
      else if (it.type === "folder") collectPlaylists(it.items, out);
    }
  }

  async function sendPlaylists() {
    const list = [];
    try {
      const user = Spicetify.Platform && Spicetify.Platform.username;
      if (user) list.push(["Tus me gusta", "spotify:user:" + user + ":collection", "https://misc.scdn.co/liked-songs/liked-songs-300.png", "Canciones guardadas"]);
    } catch (e) {}
    try {
      const root = await Spicetify.Platform.RootlistAPI.getContents();
      collectPlaylists(root && root.items, list);
    } catch (e) {
      try {
        const res = await Spicetify.CosmosAsync.get("sp://core-playlist/v1/rootlist", { policy: { folder: { rows: true, link: true }, link: true, name: true } });
        for (const r of (res && res.rows) || []) if (r.link && r.type === "playlist") list.push([r.name, r.link, toHttp(r.picture), "Playlist"]);
      } catch (e2) {
        console.warn("[gta-radio] no se pudieron leer las playlists", e, e2);
      }
    }
    sendList("playlists", list);
  }

  function sendList(kind, list) {
    const clean = (t) => String(t || "").replace(/[\t\u001e\u001f]/g, " ");
    send(kind + "\t" + list.map((f) => f.map(clean).join("\u001f")).join("\u001e"));
  }

  async function sendAlbums() {
    const list = [];
    const push = (a) => {
      if (!a || !a.uri) return;
      const artist = (a.artists && a.artists[0] && (a.artists[0].name || a.artists[0].profile && a.artists[0].profile.name)) || "";
      const imgs = a.images || (a.coverArt && a.coverArt.sources) || [];
      list.push([a.name || "Sin nombre", a.uri, pickImage(imgs), artist]);
    };
    // 1) API interna de la biblioteca (versiones nuevas de Spotify)
    try {
      const lib = Spicetify.Platform.LibraryAPI;
      for (const filters of [["0"], ["Albums"]]) {
        const res = await lib.getContents({ filters, offset: 0, limit: 500 });
        const items = ((res && res.items) || []).filter((i) => i && i.type === "album");
        if (items.length) { items.forEach(push); break; }
      }
    } catch (e) {
      console.warn("[gta-radio] LibraryAPI no disponible", e);
    }
    // 2) Web API como respaldo
    if (!list.length) {
      try {
        let url = "https://api.spotify.com/v1/me/albums?limit=50";
        for (let page = 0; url && page < 10; page++) {
          const res = await Spicetify.CosmosAsync.get(url);
          for (const it of (res && res.items) || []) push(it.album);
          url = res && res.next;
        }
      } catch (e) {
        console.warn("[gta-radio] no se pudieron leer los albumes", e);
      }
    }
    sendList("albums", list);
  }

  async function playUri(uri, autoplay) {
    try {
      await Spicetify.Player.playUri(uri);
      if (!autoplay) setTimeout(() => { if (Spicetify.Player.isPlaying()) Spicetify.Player.pause(); }, 1500);
    } catch (e) {
      console.warn("[gta-radio] no se pudo reproducir", uri, e);
    }
  }

  function handle(raw) {
    const p = String(raw).split("\t");
    switch (p[0]) {
      case "play":
        if (!Spicetify.Player.isPlaying()) Spicetify.Player.play();
        break;
      case "pause":
        if (Spicetify.Player.isPlaying()) Spicetify.Player.pause();
        break;
      case "toggle":
        Spicetify.Player.togglePlay();
        break;
      case "next":
        Spicetify.Player.next();
        break;
      case "prev":
        Spicetify.Player.back();
        break;
      case "vol":
        setNormalVolume(normalVolume() + (parseFloat(p[1]) || 0));
        break;
      case "setvol":
        setNormalVolume(parseFloat(p[1]) || 0);
        break;
      case "getplaylists":
        sendPlaylists();
        break;
      case "getalbums":
        sendAlbums();
        break;
      case "shuffle":
        try { Spicetify.Player.setShuffle(p[1] === "1"); } catch (e) { Spicetify.Player.toggleShuffle(); }
        setTimeout(sendState, 300);
        break;
      case "repeat":
        try { Spicetify.Player.setRepeat(parseInt(p[1], 10) || 0); } catch (e) { Spicetify.Player.toggleRepeat(); }
        setTimeout(sendState, 300);
        break;
      case "playuri":
        if (p[1]) playUri(p[1], p[2] === "1");
        break;
      case "duck":
        log("duck " + p[1] + " (ducked=" + ducked + ", base=" + pct(baseVolume) + ", actual=" + pct(Spicetify.Player.getVolume()) + ")");
        duck(p[1] === "1", parseFloat(p[2]));
        break;
    }
  }

  function restoreVolume() {
    clearTimeout(verifyTimer);
    if (ducked) {
      clearInterval(fadeTimer);
      if (baseVolume !== null) Spicetify.Player.setVolume(baseVolume);
      ducked = false;
      baseVolume = null;
    }
    if (restoreTarget !== null) {
      clearInterval(fadeTimer);
      Spicetify.Player.setVolume(restoreTarget);
      restoreTarget = null;
    }
  }

  function connect() {
    try {
      ws = new WebSocket(URL);
    } catch (e) {
      setTimeout(connect, RETRY_MS);
      return;
    }
    ws.onopen = () => {
      send("hello");
      sendTrack();
      sendState();
      sendVolume();
    };
    ws.onmessage = (ev) => handle(ev.data);
    ws.onclose = () => {
      ws = null;
      restoreVolume(); // si GTA se cierra con el volumen bajado, lo devolvemos
      setTimeout(connect, RETRY_MS);
    };
    ws.onerror = () => {
      try { ws.close(); } catch (e) {}
    };
  }

  Spicetify.Player.addEventListener("songchange", () => { sendTrack(); sendState(); });
  Spicetify.Player.addEventListener("onplaypause", sendState);
  connect();
})();
