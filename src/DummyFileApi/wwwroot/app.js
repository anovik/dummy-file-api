// Mirrors SizeParser on the server: binary units, KiB/MiB as aliases.
const SIZE_PATTERN = /^\s*(\d+(?:\.\d+)?)\s*(B|KB|KIB|MB|MIB)\s*$/i;
const UNITS = { B: 1, KB: 1024, KIB: 1024, MB: 1024 * 1024, MIB: 1024 * 1024 };

// Past this the in-memory blob download is worth warning about; the raw
// endpoint streams straight to disk instead.
const LARGE_DOWNLOAD_BYTES = 32 * 1024 * 1024;

const form = document.getElementById("generate-form");
const typeSelect = document.getElementById("type");
const sizeInput = document.getElementById("size");
const seedInput = document.getElementById("seed");
const downloadButton = document.getElementById("download");
const typeHint = document.getElementById("type-hint");
const sizeHint = document.getElementById("size-hint");
const statusBox = document.getElementById("status");
const errorBox = document.getElementById("error");

let typesByKey = new Map();

function parseSize(input) {
  const match = SIZE_PATTERN.exec(input ?? "");
  if (!match) {
    return null;
  }

  const bytes = Math.round(Number(match[1]) * UNITS[match[2].toUpperCase()]);
  return bytes > 0 ? bytes : null;
}

function formatBytes(bytes) {
  if (bytes >= 1024 * 1024) {
    return `${+(bytes / (1024 * 1024)).toFixed(2)} MB`;
  }
  if (bytes >= 1024) {
    return `${+(bytes / 1024).toFixed(2)} KB`;
  }
  return `${bytes} B`;
}

function withCount(bytes) {
  return `${formatBytes(bytes)} (${bytes.toLocaleString()} bytes)`;
}

function showError(message) {
  errorBox.textContent = message;
  errorBox.hidden = false;
  statusBox.hidden = true;
}

function showStatus(message) {
  statusBox.textContent = message;
  statusBox.hidden = false;
  errorBox.hidden = true;
}

function clearMessages() {
  errorBox.hidden = true;
  statusBox.hidden = true;
}

function updateTypeHint() {
  const info = typesByKey.get(typeSelect.value);
  typeHint.textContent = info
    ? `${info.mimeType} · .${info.extension} · ${withCount(info.minSizeBytes)} to ${formatBytes(info.maxSizeBytes)}`
    : "";
}

function updateSizeHint() {
  const bytes = parseSize(sizeInput.value);
  if (bytes === null) {
    sizeHint.classList.remove("warn");
    sizeHint.textContent = "Binary units: 1KB = 1,024 bytes, 1MB = 1,048,576 bytes.";
    return;
  }

  const large = bytes > LARGE_DOWNLOAD_BYTES;
  sizeHint.classList.toggle("warn", large);
  sizeHint.textContent = large
    ? `${withCount(bytes)} — this large a file is held in memory by the browser; prefer the raw endpoint below.`
    : `${withCount(bytes)}.`;
}

// Content-Disposition is same-origin here, so the filename is readable.
function filenameFrom(response, fallback) {
  const header = response.headers.get("Content-Disposition") ?? "";
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(header);
  return match ? decodeURIComponent(match[1]) : fallback;
}

async function errorMessageFrom(response) {
  try {
    const body = await response.json();
    if (body && typeof body.error === "string" && body.error.length > 0) {
      return body.error;
    }
  } catch {
    // Non-JSON body (a proxy error page, say); fall through to the status line.
  }

  return `Request failed with status ${response.status}.`;
}

function saveBlob(blob, filename) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

// Catches the common mistakes without a round-trip; the server stays the
// authority, and 429s can only surface from the response.
function localValidationError(type, bytes) {
  const info = typesByKey.get(type);
  if (!info) {
    return "Pick a format.";
  }

  if (bytes === null) {
    return "Size must look like 100KB, 1.5MB or 4096B.";
  }

  if (bytes < info.minSizeBytes) {
    return `A ${type} needs at least ${withCount(info.minSizeBytes)}.`;
  }

  if (bytes > info.maxSizeBytes) {
    return `The maximum size is ${formatBytes(info.maxSizeBytes)}.`;
  }

  return null;
}

async function loadTypes() {
  const response = await fetch("/api/files/types");
  if (!response.ok) {
    throw new Error(await errorMessageFrom(response));
  }

  const types = await response.json();
  typesByKey = new Map(types.map(t => [t.type, t]));

  typeSelect.innerHTML = "";
  for (const type of types) {
    const option = document.createElement("option");
    option.value = type.type;
    option.textContent = type.type;
    typeSelect.appendChild(option);
  }

  typeSelect.disabled = false;
  downloadButton.disabled = false;
  updateTypeHint();
}

form.addEventListener("submit", async event => {
  event.preventDefault();
  clearMessages();

  const type = typeSelect.value;
  const bytes = parseSize(sizeInput.value);

  const validationError = localValidationError(type, bytes);
  if (validationError) {
    showError(validationError);
    return;
  }

  const params = new URLSearchParams({ type, size: sizeInput.value.trim() });
  if (seedInput.value.trim() !== "") {
    params.set("seed", seedInput.value.trim());
  }

  downloadButton.disabled = true;
  showStatus(`Generating ${withCount(bytes)} of ${type}…`);

  try {
    const response = await fetch(`/api/files/generate?${params}`);
    if (!response.ok) {
      showError(await errorMessageFrom(response));
      return;
    }

    const blob = await response.blob();
    saveBlob(blob, filenameFrom(response, `dummy.${typesByKey.get(type).extension}`));
    showStatus(`Downloaded ${withCount(blob.size)} of ${type}.`);
  } catch {
    showError("Could not reach the API. Check your connection and try again.");
  } finally {
    downloadButton.disabled = false;
  }
});

typeSelect.addEventListener("change", updateTypeHint);
sizeInput.addEventListener("input", updateSizeHint);

document.getElementById("curl-origin").textContent = window.location.origin;
updateSizeHint();

loadTypes().catch(() => {
  typeSelect.innerHTML = '<option value="">Unavailable</option>';
  showError("Could not load the format list. Reload the page to try again.");
});
