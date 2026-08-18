window.blazorSqliteSample = {
  download(filename, bytes) {
    const blob = new Blob([bytes], { type: "application/vnd.sqlite3" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = filename;
    link.click();
    URL.revokeObjectURL(url);
  },

  async copy(text) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // Clipboard API needs a secure context and permission; fall back to a hidden textarea.
      const area = document.createElement("textarea");
      area.value = text;
      area.setAttribute("readonly", "");
      area.style.position = "fixed";
      area.style.opacity = "0";
      document.body.appendChild(area);
      area.select();
      const ok = document.execCommand("copy");
      document.body.removeChild(area);
      return ok;
    }
  },

  theme: {
    key: "blazorsqlite.theme",

    /** "light" | "dark" | "system" */
    get() {
      try {
        const stored = localStorage.getItem(this.key);
        return stored === "light" || stored === "dark" ? stored : "system";
      } catch {
        return "system";
      }
    },

    set(mode) {
      const root = document.documentElement;
      if (mode === "light" || mode === "dark") {
        root.setAttribute("data-theme", mode);
        try { localStorage.setItem(this.key, mode); } catch { /* ignore */ }
      } else {
        root.removeAttribute("data-theme");
        try { localStorage.removeItem(this.key); } catch { /* ignore */ }
      }
      return this.resolved();
    },

    /** What the page is actually painting right now: "light" or "dark". */
    resolved() {
      const explicit = document.documentElement.getAttribute("data-theme");
      if (explicit === "light" || explicit === "dark") {
        return explicit;
      }
      return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
    },
  },
};
