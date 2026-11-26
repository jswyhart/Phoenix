// Bundles the Electron side of Phoenix into build/: main.js and preload.js for the main process, and build/web
// (page script, styles, fonts, logo) for the window. Run with: node build.mjs
import { build } from "esbuild";
import { cpSync, mkdirSync, rmSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, "build");
rmSync(out, { recursive: true, force: true });
mkdirSync(join(out, "web"), { recursive: true });

const common = { bundle: true, sourcemap: false, logLevel: "warning", target: "es2022" };
await build({ ...common, entryPoints: [join(here, "src/main.ts")], outfile: join(out, "main.js"), platform: "node", format: "cjs", external: ["electron"] });
await build({ ...common, entryPoints: [join(here, "src/preload.ts")], outfile: join(out, "preload.js"), platform: "node", format: "cjs", external: ["electron"] });
await build({ ...common, entryPoints: [join(here, "src/webguest.ts")], outfile: join(out, "webguest.js"), platform: "node", format: "cjs", external: ["electron"] });
await build({ ...common, entryPoints: [join(here, "src/renderer/app.ts")], outfile: join(out, "web/app.js"), platform: "browser", format: "iife" });
cpSync(join(here, "static"), join(out, "web"), { recursive: true });
console.log("built", out);
