(function () {
  "use strict";

  const fallbackReleaseUrl = "https://github.com/rvenes/MeshVenes/releases/latest";
  const versionLabels = document.querySelectorAll(".version-label");
  const downloadLinks = document.querySelectorAll(".download-link");
  const releaseLinks = document.querySelectorAll(".release-link");
  const releaseNotes = document.querySelector(".release-notes");
  const releaseSizes = document.querySelectorAll(".release-size");

  function formatBytes(bytes) {
    if (!Number.isFinite(bytes) || bytes <= 0) return "Windows x64";
    return `${(bytes / 1024 / 1024).toFixed(1)} MB · Windows x64`;
  }

  function setReleaseDetails(release) {
    const version = String(release.version || "").trim();
    const downloadUrl = String(release.url || "").trim();
    const releaseUrl = String(release.releaseUrl || fallbackReleaseUrl).trim();

    if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(version) ||
        downloadUrl !== `https://venes.org/meshvenes/MeshVenes-${version}-win-x64.zip` ||
        !/^https:\/\/github\.com\/rvenes\/MeshVenes\/releases\/(latest|tag\/v\d+\.\d+\.\d+)$/.test(releaseUrl)) {
      throw new Error("The release manifest contains an unexpected version or link.");
    }

    versionLabels.forEach((label) => { label.textContent = `v${version.replace(/^v/i, "")}`; });
    downloadLinks.forEach((link) => {
      link.href = downloadUrl;
      link.setAttribute("download", "");
      link.setAttribute("aria-label", `Download MeshVenes ${version} for Windows x64`);
    });
    releaseLinks.forEach((link) => { link.href = releaseUrl; });
    releaseSizes.forEach((label) => { label.textContent = formatBytes(Number(release.sizeBytes)); });
    releaseNotes.textContent = release.notes || "See the release page for all changes in this version.";
  }

  fetch(`version.json?site=${Date.now()}`, { cache: "no-store" })
    .then((response) => {
      if (!response.ok) throw new Error(`Release manifest returned ${response.status}.`);
      return response.json();
    })
    .then(setReleaseDetails)
    .catch(() => {
      releaseNotes.textContent = "Release details are temporarily unavailable. Open GitHub to see the latest version.";
      releaseLinks.forEach((link) => { link.href = fallbackReleaseUrl; });
    });

  const imageDialog = document.getElementById("image-dialog");
  const imageDialogContent = document.getElementById("image-dialog-content");
  let pageScrollPosition = 0;
  let activeLightboxTrigger = null;

  function closeImageDialog() {
    imageDialog.hidden = true;
    document.documentElement.classList.remove("dialog-open");
    window.scrollTo(0, pageScrollPosition);
    activeLightboxTrigger?.focus({ preventScroll: true });
  }

  document.querySelectorAll("[data-lightbox]").forEach((link) => {
    link.addEventListener("click", (event) => {
      event.preventDefault();
      pageScrollPosition = window.scrollY;
      activeLightboxTrigger = link;
      imageDialogContent.src = link.href;
      imageDialogContent.alt = link.dataset.caption || link.querySelector("img")?.alt || "MeshVenes screenshot";
      document.documentElement.classList.add("dialog-open");
      imageDialog.hidden = false;
      document.querySelector(".image-dialog-close").focus({ preventScroll: true });
    });
  });

  document.querySelector(".image-dialog-close").addEventListener("click", closeImageDialog);
  imageDialog.addEventListener("click", (event) => {
    if (event.target === imageDialog) closeImageDialog();
  });
  imageDialog.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      event.preventDefault();
      closeImageDialog();
    }
  });

  document.getElementById("current-year").textContent = new Date().getFullYear();
}());
