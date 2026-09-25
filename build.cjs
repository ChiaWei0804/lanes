"use strict";
// Builds Lanes.exe in this folder, which then holds everything Lanes needs. Run: node build.cjs
// node build.cjs --release  also writes the two assets a GitHub release carries (the source archives GitHub makes lack
// Lanes.exe and node.exe): dist/Lanes-<version>.zip, everything, for new installs and for 1.0.0's updater, and
// dist/Lanes-<version>-update.zip, the same without node.exe, which the updater takes when its Node is the same.
// Lanes.exe (app/Lanes.cs, app/ui.xaml, app/lang/*.json) is compiled with the C# compiler that ships with
// Windows (.NET Framework 4), in two passes: the first build writes the icon, the second embeds it.
const { execFileSync } = require("child_process"), path = require("path"), fs = require("fs"), os = require("os");
const FW = path.join(process.env.WINDIR, "Microsoft.NET", "Framework64", "v4.0.30319");
const refs = ["WPF/PresentationFramework.dll", "WPF/PresentationCore.dll", "WPF/WindowsBase.dll", "System.Xaml.dll", "System.Net.Http.dll", "System.Web.Extensions.dll", "System.Windows.Forms.dll", "System.Drawing.dll"].map(r => `/r:${path.join(FW, r)}`);
const app = path.join(__dirname, "app");
const resources = [`/resource:${path.join(app, "ui.xaml")},ui.xaml`, ...fs.readdirSync(path.join(app, "lang")).map(file => `/resource:${path.join(app, "lang", file)},lang.${file}`)];
const csc = (exe, extra) => execFileSync(path.join(FW, "csc.exe"), ["/nologo", "/codepage:65001", "/target:winexe", `/out:${exe}`, ...extra, ...refs, ...resources, path.join(app, "Lanes.cs")], { stdio: "inherit" });

const temp = fs.mkdtempSync(path.join(os.tmpdir(), "lanes-build-"));
const tool = path.join(temp, "icon-tool.exe"), icon = path.join(temp, "Lanes.ico");
csc(tool, []);
execFileSync(tool, ["--write-icon", icon]);
csc(path.join(__dirname, "Lanes.exe"), [`/win32icon:${icon}`]);
fs.rmSync(temp, { recursive: true, force: true });

// Lanes runs on a computer without Node.js when node.exe sits in this folder; the Node running this build is copied.
const node = path.join(__dirname, "node.exe");
if (!fs.existsSync(node)) fs.copyFileSync(process.execPath, node);
console.log(`built ${path.join(__dirname, "Lanes.exe")}`);

if (process.argv.includes("--release")) {
  const { version } = JSON.parse(fs.readFileSync(path.join(__dirname, "version.json"), "utf8"));
  const dist = path.join(__dirname, "dist"), stage = path.join(dist, "Lanes");
  const full = path.join(dist, `Lanes-${version}.zip`), small = path.join(dist, `Lanes-${version}-update.zip`);
  for (const old of [stage, full, small]) fs.rmSync(old, { recursive: true, force: true });
  // What runs, plus the documentation; the source (app/, build.cjs, check.cjs) stays in the repository.
  const files = ["Lanes.exe", "node.exe", "btr-local.cjs", "version.json", ...fs.readdirSync(__dirname).filter(f => /^README.*\.md$|^LICENSE$/.test(f))];
  fs.mkdirSync(stage, { recursive: true });
  for (const file of files) fs.copyFileSync(path.join(__dirname, file), path.join(stage, file));
  fs.cpSync(path.join(__dirname, "vendor"), path.join(stage, "vendor"), { recursive: true });
  // Both packages name the Node they were built with; the repository's version.json does not change.
  const staged = path.join(stage, "version.json");
  const nodeVersion = execFileSync(node, ["--version"]).toString().trim();
  fs.writeFileSync(staged, JSON.stringify({ ...JSON.parse(fs.readFileSync(staged, "utf8")), node: nodeVersion }, null, 2) + "\n");
  // Windows' own tar writes zip archives (-a picks the format from the extension); Git Bash's tar cannot.
  const tar = path.join(process.env.WINDIR, "System32", "tar.exe");
  execFileSync(tar, ["-a", "-cf", full, "-C", dist, "Lanes"]);
  fs.rmSync(path.join(stage, "node.exe"));
  execFileSync(tar, ["-a", "-cf", small, "-C", dist, "Lanes"]);
  fs.rmSync(stage, { recursive: true, force: true });
  console.log(`release assets (Node ${nodeVersion}):\n  ${full}\n  ${small}`);
}
