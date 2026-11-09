const $ = (id) => document.getElementById(id);
let currentId = null;
let pollTimer = null;
let selectedFile = null;

/* ---------- theme ---------- */
const themeBtn = $("themeBtn");
function applyTheme(t) {
  document.documentElement.classList.toggle("dark", t === "dark");
  themeBtn.textContent = t === "dark" ? "☀️" : "🌙";
  localStorage.setItem("theme", t);
}
applyTheme(localStorage.getItem("theme") || "dark");
themeBtn.addEventListener("click", () =>
  applyTheme(document.documentElement.classList.contains("dark") ? "light" : "dark")
);

/* ---------- toast ---------- */
function toast(msg, type = "info") {
  const el = $("toast");
  const colors = { info: "bg-slate-800", ok: "bg-emerald-600", err: "bg-red-600", warn: "bg-amber-600" };
  el.innerHTML = `<div class="toast ${colors[type]} text-white px-4 py-2 rounded-xl shadow-lg text-sm">${msg}</div>`;
  el.classList.remove("hidden");
  clearTimeout(el._t);
  el._t = setTimeout(() => el.classList.add("hidden"), 3200);
}

/* ---------- health ---------- */
async function refreshHealth() {
  try {
    const h = await fetch("/healthz");
    const j = await h.json();
    $("healthBadge").textContent = "health: " + (j.status || h.status);
    $("healthBadge").className = "px-3 py-1 rounded-full text-xs font-semibold " + (h.ok ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-300" : "bg-red-100 text-red-700");
  } catch {
    $("healthBadge").textContent = "health: offline";
    $("healthBadge").className = "px-3 py-1 rounded-full text-xs font-semibold bg-red-100 text-red-700";
  }
  try {
    const r = await fetch("/readyz");
    const j = await r.json();
    $("readyBadge").textContent = "ready: " + (j.status || r.status);
    $("readyBadge").className = "px-3 py-1 rounded-full text-xs font-semibold " + (r.ok ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-300" : "bg-amber-100 text-amber-700 dark:bg-amber-900/40 dark:text-amber-300");
  } catch {
    $("readyBadge").textContent = "ready: offline";
  }
}
refreshHealth();
setInterval(refreshHealth, 10000);

/* ---------- dropzone ---------- */
const dropzone = $("dropzone");
const fileInput = $("file");
dropzone.addEventListener("click", () => fileInput.click());
dropzone.addEventListener("dragover", (e) => { e.preventDefault(); dropzone.classList.add("drag"); });
dropzone.addEventListener("dragleave", () => dropzone.classList.remove("drag"));
dropzone.addEventListener("drop", (e) => {
  e.preventDefault();
  dropzone.classList.remove("drag");
  if (e.dataTransfer.files && e.dataTransfer.files[0]) {
    fileInput.files = e.dataTransfer.files;
    handleFile(e.dataTransfer.files[0]);
  }
});
fileInput.addEventListener("change", () => {
  if (fileInput.files && fileInput.files[0]) handleFile(fileInput.files[0]);
});

function handleFile(f) {
  selectedFile = f;
  $("dropText").textContent = `${f.name} — ${(f.size / 1024).toFixed(1)} KB`;
  if (f.type.startsWith("image/")) {
    const url = URL.createObjectURL(f);
    const img = $("preview");
    img.src = url;
    img.classList.remove("hidden");
  } else {
    $("preview").classList.add("hidden");
  }
}

/* presets */
document.querySelectorAll(".preset").forEach((b) => {
  b.addEventListener("click", () => {
    $("params").value = b.dataset.preset;
  });
});

/* ---------- submit ---------- */
$("submitBtn").addEventListener("click", async () => {
  const err = $("uploadError");
  err.textContent = "";
  $("uploadInfo").textContent = "";
  if (!selectedFile) { err.textContent = "Оберіть файл"; return; }

  const form = new FormData();
  form.append("file", selectedFile);
  form.append("type", $("type").value);
  const rawParams = $("params").value.trim();
  if (rawParams) form.append("params", rawParams);

  $("submitBtn").disabled = true;
  $("submitBtn").textContent = "Завантаження…";
  try {
    const res = await fetch("/api/v1/jobs", { method: "POST", body: form });
    if (res.status === 413) { err.textContent = "Файл завеликий (413)"; toast("Файл завеликий", "err"); return; }
    if (res.status === 503) { err.textContent = "Черга повна — повторіть пізніше (503)"; toast("Черга повна", "warn"); return; }
    if (!res.ok) {
      const t = await res.text();
      err.textContent = `Помилка ${res.status}: ${t}`;
      toast("Помилка завантаження", "err");
      return;
    }
    const dto = await res.json();
    currentId = dto.id;
    $("jobCard").classList.remove("hidden");
    $("emptyState").classList.add("hidden");
    $("jobId").textContent = dto.id;
    $("rowsWrap").classList.add("hidden");
    $("rowsOut").textContent = "";
    $("resultPreview").classList.add("hidden");
    $("uploadInfo").textContent = `Прийнято: ${dto.id}`;
    toast("Завдання створено", "ok");
    startPolling();
    loadList();
  } catch (e) {
    err.textContent = "Мережева помилка: " + e.message;
    toast("Мережева помилка", "err");
  } finally {
    $("submitBtn").disabled = false;
    $("submitBtn").textContent = "Завантажити і запустити";
  }
});

function startPolling() {
  if (pollTimer) clearInterval(pollTimer);
  poll();
  pollTimer = setInterval(poll, 1000);
}

async function poll() {
  if (!currentId) return;
  try {
    const res = await fetch(`/api/v1/jobs/${currentId}/status`);
    if (!res.ok) { $("jobStatus").textContent = "not found"; return; }
    const j = await res.json();
    $("jobJson").textContent = JSON.stringify(j, null, 2);
    const status = j.status;
    $("jobStatus").textContent = status;
    const cls = status === "Succeeded" ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-300"
      : (status === "Failed" || status === "Canceled") ? "bg-red-100 text-red-700 dark:bg-red-900/40 dark:text-red-300"
      : "bg-amber-100 text-amber-700 dark:bg-amber-900/40 dark:text-amber-300";
    $("jobStatus").className = "px-3 py-1 rounded-full text-xs font-semibold " + cls;
    $("jobMeta").textContent = `type=${j.type} · progress=${j.progress}% · allocated=${j.allocatedBytes} B · upload=${j.uploadSize} B`;
    $("progressBar").style.width = j.progress + "%";

    $("downloadBtn").disabled = status !== "Succeeded";
    $("rowsBtn").disabled = j.type !== "csv";

    if (status === "Succeeded" && j.type === "image") {
      $("resultImg").src = `/api/v1/jobs/${currentId}/result?t=${Date.now()}`;
      $("resultPreview").classList.remove("hidden");
    }

    const terminal = status === "Succeeded" || status === "Failed" || status === "Canceled";
    if (terminal && pollTimer) {
      clearInterval(pollTimer);
      pollTimer = null;
      if (status === "Succeeded") toast("Завдання виконано", "ok");
      else if (status === "Failed") toast("Завдання провалено: " + (j.error || ""), "err");
      else toast("Завдання скасовано", "warn");
      loadList();
    }
  } catch (e) {
    $("jobStatus").textContent = "poll error";
  }
}

$("downloadBtn").addEventListener("click", () => {
  if (!currentId) return;
  window.location.href = `/api/v1/jobs/${currentId}/result`;
});

$("rowsBtn").addEventListener("click", async () => {
  if (!currentId) return;
  $("rowsWrap").classList.remove("hidden");
  $("rowsOut").textContent = "Стрімінг…";
  let out = "";
  try {
    const res = await fetch(`/api/v1/jobs/${currentId}/rows`);
    if (!res.ok) { $("rowsOut").textContent = `Помилка ${res.status}`; return; }
    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      out += decoder.decode(value, { stream: true });
      $("rowsOut").textContent = out;
    }
    toast("Рядки отримано", "ok");
  } catch (e) {
    $("rowsOut").textContent = "Помилка: " + e.message;
    toast("Помилка стрімінгу", "err");
  }
});

$("deleteBtn").addEventListener("click", async () => {
  if (!currentId) return;
  if (!confirm("Видалити завдання і файли?")) return;
  const res = await fetch(`/api/v1/jobs/${currentId}`, { method: "DELETE" });
  $("jobStatus").textContent = res.ok ? "deleted" : `delete ${res.status}`;
  if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
  toast(res.ok ? "Видалено" : "Помилка видалення", res.ok ? "ok" : "err");
  loadList();
});

/* ---------- list ---------- */
async function loadList() {
  try {
    const res = await fetch("/api/v1/jobs?limit=30&offset=0");
    const j = await res.json();
    const list = $("jobList");
    list.innerHTML = "";
    (j.items || []).forEach((it) => {
      const div = document.createElement("div");
      div.className = "flex items-center justify-between gap-2 p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 cursor-pointer";
      const statusColor = it.status === "Succeeded" ? "text-emerald-500" : (it.status === "Failed" || it.status === "Canceled") ? "text-red-500" : "text-amber-500";
      div.innerHTML = `<div class="min-w-0">
        <div class="font-mono text-xs truncate">${it.id}</div>
        <div class="text-xs text-slate-500 dark:text-slate-400">${it.type} · <span class="${statusColor}">${it.status}</span></div>
      </div>
      <div class="text-xs text-slate-400 shrink-0">${new Date(it.createdAt).toLocaleTimeString()}</div>`;
      div.addEventListener("click", () => {
        currentId = it.id;
        $("jobCard").classList.remove("hidden");
        $("emptyState").classList.add("hidden");
        $("jobId").textContent = it.id;
        startPolling();
      });
      list.appendChild(div);
    });
    if (!(j.items && j.items.length)) {
      list.innerHTML = '<div class="text-xs text-slate-400">Порожньо</div>';
    }
  } catch (e) {
    toast("Не вдалося завантажити список", "err");
  }
}
$("listBtn").addEventListener("click", loadList);
loadList();
