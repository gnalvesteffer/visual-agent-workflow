import { copyFile } from "node:fs/promises";
import { resolve } from "node:path";

const packageRoot = resolve(import.meta.dirname, "../node_modules/monaco-editor");
const outputRoot = resolve(import.meta.dirname, "../../../src/VisualAgentWorkflow.Studio/wwwroot/lib/monaco");
await copyFile(resolve(packageRoot, "LICENSE"), resolve(outputRoot, "MONACO-LICENSE.txt"));
