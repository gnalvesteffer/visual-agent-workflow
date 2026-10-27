let monacoPromise;
let stylesheetPromise;
const editors = new WeakMap();

export async function create(host, value, dotNet) {
    await ensureStylesheet();
    await (monacoPromise ??= import("./lib/monaco/vaw-monaco.js"));
    const monaco = globalThis.VAWMonaco;
    if (!monaco) throw new Error("Monaco did not initialize correctly.");
    dispose(host);
    applyTheme(host, monaco);

    const editor = monaco.editor.create(host, {
        value,
        language: "markdown",
        theme: "vaw-app-theme",
        ariaLabel: "Markdown source",
        automaticLayout: true,
        minimap: { enabled: false },
        lineNumbers: "on",
        lineNumbersMinChars: 3,
        glyphMargin: false,
        folding: true,
        wordWrap: "on",
        scrollBeyondLastLine: false,
        renderWhitespace: "selection",
        tabSize: 2,
        insertSpaces: true,
        fontSize: 13,
        lineHeight: 20,
        padding: { top: 10, bottom: 10 },
        stickyScroll: { enabled: false },
        guides: { indentation: true, bracketPairs: false }
    });

    let changeTimer;
    let changePending = false;
    const changeSubscription = editor.onDidChangeModelContent(() => {
        changePending = true;
        window.clearTimeout(changeTimer);
        changeTimer = window.setTimeout(() => {
            notifyChanges();
        }, 100);
    });
    const blurSubscription = editor.onDidBlurEditorWidget(notifyChanges);
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
        clearPendingChange();
        dotNet.invokeMethodAsync("SaveMarkdownFromEditor", editor.getValue()).catch(() => {});
    });

    const themeObserver = new MutationObserver(() => applyTheme(host, monaco));
    themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ["data-vaw-theme"] });
    editors.set(host, { editor, changeSubscription, blurSubscription, themeObserver, changeTimer: () => changeTimer, clearPendingChange, isChangePending: () => changePending, dotNet });
    editor.focus();

    function clearPendingChange() {
        window.clearTimeout(changeTimer);
        changePending = false;
    }

    function notifyChanges() {
        if (!changePending) return;
        clearPendingChange();
        dotNet.invokeMethodAsync("MarkdownEditorChanged", editor.getValue()).catch(() => {});
    }
}

export function setValue(host, value) {
    const instance = editors.get(host);
    const model = instance?.editor.getModel();
    if (model && model.getValue() !== value) instance.editor.setValue(value);
}

export function getValue(host) {
    const instance = editors.get(host);
    if (!instance) return "";
    instance.clearPendingChange();
    return instance.editor.getValue();
}

export async function dispose(host) {
    const instance = editors.get(host);
    if (!instance) return;
    window.clearTimeout(instance.changeTimer());
    if (instance.isChangePending())
        await instance.dotNet.invokeMethodAsync("MarkdownEditorChanged", instance.editor.getValue()).catch(() => {});
    instance.changeSubscription.dispose();
    instance.blurSubscription.dispose();
    instance.themeObserver.disconnect();
    instance.editor.dispose();
    editors.delete(host);
}

function ensureStylesheet() {
    if (stylesheetPromise) return stylesheetPromise;
    stylesheetPromise = new Promise((resolve, reject) => {
        const link = document.createElement("link");
        link.rel = "stylesheet";
        link.href = new URL("./lib/monaco/vaw-monaco.css", import.meta.url).href;
        link.onload = resolve;
        link.onerror = () => {
            link.remove();
            stylesheetPromise = null;
            reject(new Error("Monaco styles could not be loaded."));
        };
        document.head.append(link);
    });
    return stylesheetPromise;
}

function applyTheme(host, monaco) {
    const variables = getComputedStyle(host);
    const themeName = document.documentElement.dataset.vawTheme;
    const light = themeName === "light" || themeName === "github-light";
    const base = light ? "vs" : "vs-dark";
    monaco.editor.defineTheme("vaw-app-theme", {
        base,
        inherit: true,
        rules: [],
        colors: {
            "editor.background": variables.getPropertyValue("--vaw-color-canvas").trim(),
            "editor.foreground": variables.getPropertyValue("--vaw-color-text").trim(),
            "editorLineNumber.foreground": variables.getPropertyValue("--vaw-color-muted").trim(),
            "editorLineNumber.activeForeground": variables.getPropertyValue("--vaw-color-text").trim(),
            "editorCursor.foreground": variables.getPropertyValue("--vaw-color-focus").trim(),
            "editor.lineHighlightBackground": variables.getPropertyValue("--vaw-color-surface").trim(),
            "editor.selectionBackground": light ? "#add6ff" : "#264f78",
            "editor.inactiveSelectionBackground": light ? "#e5ebf1" : "#3a3d41",
            "editorIndentGuide.background1": variables.getPropertyValue("--vaw-color-border").trim(),
            "editorGutter.background": variables.getPropertyValue("--vaw-color-canvas").trim()
        }
    });
    monaco.editor.setTheme("vaw-app-theme");
}
