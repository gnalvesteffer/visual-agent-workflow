import * as monaco from "monaco-editor/editor/editor.api.js";
import { conf as markdownConfiguration, language as markdownLanguage } from "monaco-editor/languages/definitions/markdown/markdown.js";
import EditorWorker from "monaco-editor/editor/editor.worker.js?worker";

self.MonacoEnvironment = {
  getWorker: () => new EditorWorker()
};

monaco.languages.register({ id: "markdown" });
monaco.languages.setLanguageConfiguration("markdown", markdownConfiguration);
monaco.languages.setMonarchTokensProvider("markdown", markdownLanguage);
globalThis.VAWMonaco = monaco;
