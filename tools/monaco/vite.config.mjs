import { defineConfig } from "vite";
import { resolve } from "node:path";

export default defineConfig({
  base: "./",
  build: {
    outDir: resolve(import.meta.dirname, "../../src/VisualAgentWorkflow.Studio/wwwroot/lib/monaco"),
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 3000,
    target: "es2022",
    rollupOptions: {
      input: resolve(import.meta.dirname, "src/monaco.js"),
      output: {
        entryFileNames: "vaw-monaco.js",
        chunkFileNames: "chunks/[name]-[hash].js",
      assetFileNames: (assetInfo) => assetInfo.name?.endsWith(".css")
        ? "vaw-monaco.css"
        : "assets/[name]-[hash][extname]"
      }
    }
  },
  worker: { format: "es" }
});
