/* SpokaPilote — menu mobile et liens de téléchargement de la dernière version (API GitHub, repli : page des Releases). */
(function () {
  "use strict";
  var REPO = "Spokator/SpokaPilote";

  var toggle = document.querySelector(".nav-toggle");
  var menu = document.getElementById("menu");
  if (toggle && menu) {
    toggle.addEventListener("click", function () {
      var open = menu.classList.toggle("open");
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
    });
  }

  var links = document.querySelectorAll("[data-download]");
  if (!links.length) return;

  function formatSize(bytes) {
    return (bytes / 1048576).toLocaleString("fr-FR", { maximumFractionDigits: 0 }) + " Mo";
  }
  function setText(selector, text) {
    var nodes = document.querySelectorAll(selector);
    for (var i = 0; i < nodes.length; i++) nodes[i].textContent = text;
  }

  // PC ARM64 (Snapdragon, Surface) : le bouton principal propose l'installeur ARM64.
  var archReady = Promise.resolve();
  if (navigator.userAgentData && navigator.userAgentData.getHighEntropyValues) {
    archReady = navigator.userAgentData.getHighEntropyValues(["architecture", "bitness"]).then(function (v) {
      if (v.architecture !== "arm") return;
      var primary = document.querySelectorAll("[data-download-primary]");
      for (var i = 0; i < primary.length; i++) {
        primary[i].setAttribute("data-download", "arm64");
        var label = primary[i].querySelector("[data-label]");
        if (label) label.textContent = "Télécharger pour Windows ARM64";
      }
      var secondary = document.querySelectorAll("[data-download-secondary]");
      for (var j = 0; j < secondary.length; j++) {
        secondary[j].setAttribute("data-download", "x64");
        secondary[j].textContent = "Version Intel / AMD (x64)";
      }
    })["catch"](function () {});
  }

  archReady.then(function () {
    return fetch("https://api.github.com/repos/" + REPO + "/releases/latest", { headers: { Accept: "application/vnd.github+json" } });
  }).then(function (r) {
    if (!r.ok) throw new Error("HTTP " + r.status);
    return r.json();
  }).then(function (release) {
    var assets = release.assets || [];
    function find(re) { for (var i = 0; i < assets.length; i++) if (re.test(assets[i].name)) return assets[i]; return null; }
    var byArch = { x64: find(/^SpokaPilote-Setup-[\d.]+\.exe$/i), arm64: find(/-arm64\.exe$/i) };
    var version = String(release.tag_name || "").replace(/^v/, "");
    if (version) setText("[data-version]", version);
    if (release.published_at) setText("[data-release-date]", new Date(release.published_at).toLocaleDateString("fr-FR", { day: "numeric", month: "long", year: "numeric" }));

    var all = document.querySelectorAll("[data-download]");
    for (var i = 0; i < all.length; i++) {
      var asset = byArch[all[i].getAttribute("data-download")];
      if (asset) all[i].href = asset.browser_download_url;
    }
    ["x64", "arm64"].forEach(function (arch) {
      var a = byArch[arch];
      if (!a) return;
      setText("[data-size=\"" + arch + "\"]", formatSize(a.size));
      setText("[data-name=\"" + arch + "\"]", a.name);
      if (a.digest) setText("[data-sha=\"" + arch + "\"]", "SHA-256 : " + a.digest.replace(/^sha256:/, ""));
    });
  })["catch"](function () {
    /* Hors ligne ou limite de l'API atteinte : les liens pointent déjà vers la dernière Release. */
  });
})();
