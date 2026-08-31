; 天学网答案提取 · Inno Setup 安装脚本
; 产物：单个 setup.exe 安装应用本体（Program Files），允许各类文件存在；
; 天学网客户端与程序本体在文件夹尺度上分立存放（客户端由用户在首次启动时自选目录安装）。
; 编译：ISCC.exe installer.iss

#define MyAppName "天学网答案提取"
#define MyAppVersion "1.0.0"
#define MyAppExe "天学网答案提取.exe"

[Setup]
AppId={{8F3B2C41-5E62-4A8D-B7E0-7E0A1C2D3E4F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher="TxwExtract"
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 显式启用"选择安装位置"页：程序本体安装目录由用户在向导中自行选择（默认 Program Files）
DisableDirPage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=dist
OutputBaseFilename=天学网答案提取_Setup_v{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=src\app.ico
UninstallDisplayIcon={app}\{#MyAppExe}
VersionInfoVersion=1.0.0.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} 安装程序
ShowLanguageDialog=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; 自包含发布产物（含 .NET 8 运行时与 WinRT OCR 依赖，目标机无需预装 .NET）
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 内置 Tesseract OCR 引擎（主用，无需联网），含 chi_sim+eng 语言包
Source: "tesseract\*"; DestDir: "{app}\tesseract"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// 客户端目录分立说明：直接写入完成页，不再额外弹框（避免"安装完成"提醒两次）
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption :=
      '天学网答案提取 已安装完成。' #13#13 +
      '天学网客户端与程序本体是分开存放的：首次使用「自动回答」页并点击【启动客户端】时，' +
      '程序会引导你选择客户端的安装目录（之后自动记住）。' #13#13 +
      '运行数据（题库索引、导出、抓包日志等）保存在：' #13 +
      '%LOCALAPPDATA%\TxwExtract';
end;
