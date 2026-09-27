"use strict";
// Builds Lanes.exe in this folder, which then holds everything Lanes needs. Run: node build.cjs
// node build.cjs --release  also writes the asset a GitHub release carries (the source archives GitHub makes lack
// Lanes.exe): dist/Lanes-<version>.zip, for new installs and for the updater.
// Lanes.exe (app/*.cs, app/ui.xaml, app/lang/*.json, app/page/*.js) is compiled with the C# compiler that ships with
// Windows (.NET Framework 4), in two passes: the first build writes the icon, the second embeds it. Node is needed
// only here and in check.cjs; Lanes itself does not use it.
const { execFileSync } = require("child_process"), path = require("path"), fs = require("fs"), os = require("os");
const FW = path.join(process.env.WINDIR, "Microsoft.NET", "Framework64", "v4.0.30319");
const refs = ["WPF/PresentationFramework.dll", "WPF/PresentationCore.dll", "WPF/WindowsBase.dll", "System.Xaml.dll", "System.Net.Http.dll", "System.Web.Extensions.dll", "System.Windows.Forms.dll", "System.Drawing.dll", "System.IO.Compression.dll", "System.IO.Compression.FileSystem.dll"].map(r => `/r:${path.join(FW, r)}`);
const app = path.join(__dirname, "app");
const resources = [
  `/resource:${path.join(app, "ui.xaml")},ui.xaml`,
  ...fs.readdirSync(path.join(app, "lang")).map(file => `/resource:${path.join(app, "lang", file)},lang.${file}`),
  ...fs.readdirSync(path.join(app, "page")).map(file => `/resource:${path.join(app, "page", file)},page.${file}`)
];
const sources = fs.readdirSync(app).filter(file => file.endsWith(".cs")).map(file => path.join(app, file));
const csc = (exe, extra) => execFileSync(path.join(FW, "csc.exe"), ["/nologo", "/codepage:65001", "/target:winexe", `/out:${exe}`, ...extra, ...refs, ...resources, ...sources], { stdio: "inherit" });

const temp = fs.mkdtempSync(path.join(os.tmpdir(), "lanes-build-"));
const tool = path.join(temp, "icon-tool.exe"), icon = path.join(temp, "Lanes.ico");
csc(tool, []);
execFileSync(tool, ["--write-icon", icon]);
csc(path.join(__dirname, "Lanes.exe"), [`/win32icon:${icon}`]);
fs.rmSync(temp, { recursive: true, force: true });
console.log(`built ${path.join(__dirname, "Lanes.exe")}`);

if (process.argv.includes("--release")) {
  const { version } = JSON.parse(fs.readFileSync(path.join(__dirname, "version.json"), "utf8"));
  const dist = path.join(__dirname, "dist"), stage = path.join(dist, "Lanes"), zip = path.join(dist, `Lanes-${version}.zip`);
  for (const old of [stage, zip]) fs.rmSync(old, { recursive: true, force: true });
  // What runs, plus the licenses (BTR's MIT notice sits in vendor/btr); the README and the source (app/, build.cjs,
  // check.cjs) stay in the repository.
  fs.mkdirSync(stage, { recursive: true });
  for (const file of ["Lanes.exe", "version.json", "LICENSE"]) fs.copyFileSync(path.join(__dirname, file), path.join(stage, file));
  fs.cpSync(path.join(__dirname, "vendor"), path.join(stage, "vendor"), { recursive: true });
  // Windows' own tar writes zip archives (-a picks the format from the extension); Git Bash's tar cannot.
  execFileSync(path.join(process.env.WINDIR, "System32", "tar.exe"), ["-a", "-cf", zip, "-C", dist, "Lanes"]);
  fs.rmSync(stage, { recursive: true, force: true });
  console.log(`release asset:\n  ${zip}`);
}
