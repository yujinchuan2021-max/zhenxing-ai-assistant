#define MyAppName "枕星 AI 助手"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "yujinchuan2021-max"
#define MyAppExeName "TubaWinUi3.exe"
#define MyAppCopyright "Copyright (C) 2026 yujinchuan2021-max; upstream notices in License.txt"

[Setup]
AppId={{92B08A5C-463A-45B8-B03A-875D9C348180}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}_arm64
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://zhenxingai.com
AppSupportURL=mailto:yujinchuan2021@gmail.com
AppCopyright={#MyAppCopyright}
DefaultDirName={autopf}\Zhenxing AI Assistant
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=License.txt
OutputDir=SetupOutput
OutputBaseFilename=TubaWinUi3_Setup_{#MyAppVersion}_arm64
SetupIconFile=TubaWinUi3.WinUI3\Assets\AppIcon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} (ARM64)
PrivilegesRequired=admin
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
LanguageDetectionMethod=locale
ShowLanguageDialog=no
UpdateUninstallLogAppName=yes
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousSetupType=yes
UsePreviousTasks=yes
DisableDirPage=no
DirExistsWarning=no
AppendDefaultDirName=yes

; [A08] 覆盖升级：旧卸载日志里的「整根删除安装目录」记录必须被丢弃 ================
; 旧版脚本的 [UninstallDelete] 里有 `Type: filesandordirs; Name: "{app}"`（递归删除整个安装目录）。
; 按官方安装顺序文档，[UninstallDelete] 的条目在安装时被写进卸载日志（unins???.dat），
; 因此「旧版→新版覆盖安装→卸载时选择保留数据」时，旧日志里的整根删除记录仍会被执行——
; 只在新的 .iss 里删掉那一行，修不了升级路径。
; 处理方式（官方支持的指令，不手改二进制日志、不调用有缺陷的旧卸载器）：
;   · 同 AppId + 同安装目录的覆盖升级：UninstallLogMode=overwrite 覆盖旧日志，丢弃旧记录；
;   · 枕星发行使用独立 AppId；后续枕星升级必须保持该值，不能复用上游 CE 的身份；
;   · overwrite 只替换「同 AppId + 同安装模式/架构」的旧日志：只有一份且身份确认时放行；
;     身份不一致（其它应用 / 其它架构，例如旧 arm64 AppId 装出的日志后来被 x64 装进同一目录）
;     时旧日志必然残留，直接中止并提示改用新目录或先清理旧日志；
;     无法确认身份（头部读不出）或多于一份时默认中止（默认按钮「否」）——
;     由 [Code] 的 TubaCheckUninstallLogs 在 PrepareToInstall 中读取日志头部（格式标记 + AppId）判定。
;   · 代价：旧版独有的程序文件可能残留（残留只能由用户自行清理，绝不能用「删除用户数据」消除）。
; 官方文档对 overwrite 标注 not recommended（一般场景更希望保留旧记录）；本项目优先保证安全，
; 故在此明确采用。依据：
;   https://jrsoftware.org/ishelp/topic_setup_uninstalllogmode.htm
;   https://jrsoftware.org/ishelp/topic_appendnotes.htm
;   https://jrsoftware.org/ishelp/topic_installorder.htm
UninstallLogMode=overwrite

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Messages]
SetupAppTitle=安装 - {#MyAppName}
SetupWindowTitle=安装 - {#MyAppName}
WelcomeLabel2=此向导将引导您完成 [name/ver] 的安装过程。%n%n建议在继续之前关闭所有其他应用程序，以便安装程序更新相关的系统文件，无需重新启动计算机。
SelectDirBrowseLabel=如需安装到其他位置，请单击"浏览"选择目标文件夹。%n%n点击"安装"开始安装。
DiskSpaceWarning=至少需要 %1 KB 的可用空间才能安装，但所选驱动器只有 %2 KB 可用。%n%n是否仍要继续？
SelectStartMenuFolderBrowseLabel=如需选择其他文件夹，请单击"浏览"。%n%n点击"安装"开始安装。
ReadyLabel2a=单击"安装"开始安装，或单击"上一步"修改设置。
ReadyLabel2b=单击"安装"开始安装。
FinishedLabel=[name] 已成功安装到您的计算机中。
FinishedLabelNoIcons=[name] 已成功安装到您的计算机中。

ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&R)...
ButtonWizardBrowse=浏览(&R)...
ButtonNewFolder=新建文件夹(&M)

SelectLanguageTitle=选择安装语言
SelectLanguageLabel=选择安装过程中使用的语言：

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "publish_arm64_installer\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; [A08] 卸载时不再递归删除安装目录：程序文件由 Inno 的「已安装文件清单」移除；
; 用户数据（安装目录内的 Data 子目录、.config_location 标记文件与自定义数据目录）
; 由卸载脚本（[Code] 段）按“是否删除用户数据”的明确选择单独处理，默认保留数据。
; 本段只删除安装器自己写入、且不在卸载清单里的安装状态标记文件。
Type: files; Name: "{app}\.installed"

[Code]
var
  CustomPrevPath: String;

function InitializeSetup: Boolean;
begin
  Result := True;
  CustomPrevPath := '';
  if not RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{92B08A5C-463A-45B8-B03A-875D9C348180}_is1',
    'InstallLocation', CustomPrevPath) then
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{92B08A5C-463A-45B8-B03A-875D9C348180}_is1',
    'Inno Setup: App Path', CustomPrevPath);
end;

procedure CurWizardChanged(CurPageID: Integer);
begin
  if (CurPageID = wpSelectDir) and (CustomPrevPath <> '') then
  begin
    WizardForm.DirEdit.Text := CustomPrevPath;
    CustomPrevPath := '';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\.installed'), 'installed', False);
end;

function IsWindowsVersionOk: Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  // Windows 10 2004/20H1 = Build 19041 (与 MSIX 商店版门禁一致)
  Result := (Version.Major > 10) or
            ((Version.Major = 10) and (Version.Build >= 19041));
end;

// [A08] 覆盖升级前的历史卸载日志检查（第三次复核缺口③）====================================
// 事实依据（官方文档 + 实测）：
//   · UninstallLogMode=overwrite 只会替换「同一份应用、同一目录」的旧日志 —— 官方 [Setup] AppId 文档：
//     "The value of AppId is stored inside uninstall log files (unins???.dat), and is checked by
//      subsequent installations to determine whether it may append to a particular existing
//      uninstall log."；AppendNotes 亦为 "an existing uninstall log file that belongs to the same
//      application and is in the same directory"。
//   · 因此「数一数目录里有几份日志」不是迁移验证：只有一份但身份不同的日志（例如旧 arm64 AppId
//     装出的日志，后来用 x64 装进同一目录）不会被覆盖，旧记录里的「卸载时删除整个安装目录」照样生效。
// 做法：逐份读取日志头部的身份（纯 ASCII 的前两个字段；已在真实 unins*.dat 上核对过）：
//   字段 1 = 日志格式标记，安装器里只有两种字面量：
//            "Inno Setup Uninstall Log (b)"          ← 32 位安装模式
//            "Inno Setup Uninstall Log (b) 64-bit"   ← 64 位安装模式（x64 与 arm64 都是 64 位模式）
//   字段 2 = 写这份日志的安装器的 AppId（与卸载注册表键名 "<AppId>_is1" 同值）
// 判定（同 AppId + 同安装模式/架构才算确认）：
//   · 没有日志，或只有一份且身份确认 → 放行（官方支持的覆盖升级路径）；
//   · 日志的 AppId 与本安装包不同（另一个应用）→ 直接中止，提示改用新目录或先清理旧日志；
//   · AppId 相同但模式/架构标记不同（或格式不认识）→ 默认中止（默认按钮「否」）；
//   · 存在无法确认的日志（头部读不出），或多于一份 → 默认中止（默认按钮「否」）。
//   默认中止的三种情形都可以由用户明确确认后继续；AppId 不一致（另一个应用）没有「继续」选项。
function TubaNextLogField(const Data: AnsiString; var At: Integer; const MaxAt: Integer): String;
var
  S: String;
begin
  // 取一个 #0 结尾的 ASCII 字段，并把游标推进到下一个非 #0 字节。
  // 只看头部固定块（日志格式 (b) = 两个 64 字节块）：不越界读取后续数据，同时限长避免拼接开销。
  S := '';
  while (At <= Length(Data)) and (At <= MaxAt) and (Ord(Data[At]) <> 0) and (Length(S) < 128) do
  begin
    S := S + Chr(Ord(Data[At]));
    At := At + 1;
  end;
  while (At <= Length(Data)) and (At <= MaxAt) and (Ord(Data[At]) = 0) do
    At := At + 1;
  Result := S;
end;

// 读取一份卸载日志的身份；无法确认（不可读 / 太短 / 格式不认识）→ False
function TubaReadLogIdentity(const Path: String; var Tag, AppId: String): Boolean;
var
  Data: AnsiString;
  At: Integer;
begin
  Result := False;
  Tag := '';
  AppId := '';
  if not LoadStringFromFile(Path, Data) then Exit;
  At := 1;
  Tag := TubaNextLogField(Data, At, 64);       // 字段 1 = 头部第 1 个 64 字节块
  AppId := TubaNextLogField(Data, At, 128);    // 字段 2 = 头部第 2 个 64 字节块
  if CompareText(Copy(Tag, 1, 24), 'Inno Setup Uninstall Log') <> 0 then Exit;
  if AppId = '' then Exit;
  Result := True;
end;

// 本安装器的日志格式标记（模式/架构）：与 Inno 的 uninstaller 实现一致，只有 64 位安装模式带后缀
function TubaSelfLogTag: String;
begin
  if Is64BitInstallMode then
    Result := 'Inno Setup Uninstall Log (b) 64-bit'
  else
    Result := 'Inno Setup Uninstall Log (b)';
end;

// 本安装器的 AppId：编译期从本文件的 [Setup] 取原值（四份脚本这一行文本完全一致，各文件展开成各自的值），
// 再按 [Setup] 的转义规则把开头的 '{{' 还原成 '{'（与写进日志、写进卸载注册表键名的实际值一致）。
function TubaSelfAppId: String;
var
  S: String;
begin
  S := '{#SetupSetting("AppId")}';
  if Copy(S, 1, 2) = '{{' then
    S := Copy(S, 2, MaxInt);
  Result := S;
end;

// 需要用户显式确认才继续的场合：默认按钮是「否」（静默安装取默认 → 中止）；返回 '' = 继续安装
function TubaConfirmRiskyOverwrite(const Detail, CancelMsg: String): String;
begin
  if SuppressibleMsgBox(Detail + #13#10 + #13#10 +
      '仍要继续安装吗？（不推荐）',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES then
    Result := CancelMsg
  else
    Result := '';
end;

// 覆盖升级前的历史卸载日志检查：返回 '' = 允许直接覆盖；非空 = 中止安装的原因
function TubaCheckUninstallLogs(const Dir: String): String;
var
  FindRec: TFindRec;
  Base, Path, Tag, AppId, SelfTag, SelfId: String;
  Found, Confirmed, Foreign, Mismatch, Unreadable: Integer;
begin
  Result := '';
  Base := Dir;
  while (Length(Base) > 1) and ((Base[Length(Base)] = '\') or (Base[Length(Base)] = '/')) do
    Base := Copy(Base, 1, Length(Base) - 1);
  if (Base = '') or (not DirExists(Base)) then Exit;

  SelfTag := TubaSelfLogTag;
  SelfId := TubaSelfAppId;
  Found := 0;
  Confirmed := 0;
  Foreign := 0;
  Mismatch := 0;
  Unreadable := 0;
  if FindFirst(Base + '\unins???.dat', FindRec) then
  begin
    repeat
      if (FindRec.Attributes and 16) = 0 then      // 16 = FILE_ATTRIBUTE_DIRECTORY
      begin
        Found := Found + 1;
        Path := Base + '\' + FindRec.Name;
        if TubaReadLogIdentity(Path, Tag, AppId) then
        begin
          if CompareText(AppId, SelfId) <> 0 then
          begin
            // 另一个应用（AppId 不同）：overwrite 一定不会替换它 → 直接中止
            Foreign := Foreign + 1;
            Log('A08: 历史卸载日志属于其它应用（AppId 不一致）: ' + Path
                + ' [日志: ' + Tag + ' | ' + AppId + '] [本安装器: ' + SelfTag + ' | ' + SelfId + ']');
          end
          else if CompareText(Tag, SelfTag) = 0 then
            Confirmed := Confirmed + 1
          else
          begin
            // 同一个 AppId，但安装模式/架构标记不同（或格式不认识）：同样不会被替换 → 默认中止
            Mismatch := Mismatch + 1;
            Log('A08: 历史卸载日志的安装模式/架构与本安装包不一致: ' + Path
                + ' [日志: ' + Tag + ' | ' + AppId + '] [本安装器: ' + SelfTag + ' | ' + SelfId + ']');
          end;
        end
        else
        begin
          Unreadable := Unreadable + 1;
          Log('A08: 历史卸载日志头部无法确认: ' + Path);
        end;
      end;
    until not FindNext(FindRec);
    FindClose(FindRec);
  end;

  if Found = 0 then
  begin
    Log('A08: 目标目录没有历史卸载日志，无需检查: ' + Base);
    Exit;
  end;
  if Foreign > 0 then
  begin
    Result := '目标安装目录中存在【其它应用或其它架构】留下的卸载记录（unins*.dat，共 '
        + IntToStr(Foreign) + ' 份：AppId 或安装模式与本安装包不一致）。' + #13#10#13#10
        + '覆盖安装只会替换「同一应用」的旧日志，这些记录会原样留下——'
        + '它们可能包含「卸载时删除整个安装目录」之类的旧动作。' + #13#10#13#10
        + '请改用全新的安装目录安装；若确实要复用该目录，请先手动卸载/清理这些旧日志后重试。';
    Exit;
  end;
  if Mismatch > 0 then
  begin
    Result := TubaConfirmRiskyOverwrite('目标安装目录中存在 ' + IntToStr(Mismatch)
        + ' 份本产品但安装模式/架构不同的历史卸载日志（unins*.dat）。' + #13#10
        + '覆盖安装不会替换它们，旧记录（可能包含「卸载时删除整个安装目录」）仍会在卸载时生效。'
        + #13#10 + '建议改用新的安装目录，或先手动清理这些旧卸载日志后再安装。',
        '检测到安装模式/架构不一致的历史卸载日志，安装已取消。请更换安装目录，'
        + '或清理旧卸载日志（unins*.dat）后重试。');
    Exit;
  end;
  if Unreadable > 0 then
  begin
    Result := TubaConfirmRiskyOverwrite('目标安装目录中存在 ' + IntToStr(Found)
        + ' 份历史卸载日志（unins*.dat），其中 ' + IntToStr(Unreadable)
        + ' 份的归属无法确认（头部读不出或格式不认识）。' + #13#10
        + '无法确认这些旧记录是否包含「卸载时删除整个安装目录」。' + #13#10
        + '建议改用新的安装目录，或先手动清理这些旧卸载日志后再安装。',
        '检测到无法确认归属的历史卸载日志，安装已取消。请更换安装目录，'
        + '或清理旧卸载日志（unins*.dat）后重试。');
    Exit;
  end;
  if Found > 1 then
  begin
    Result := TubaConfirmRiskyOverwrite('目标安装目录中存在 ' + IntToStr(Found)
        + ' 份历史卸载日志（unins*.dat）。' + #13#10
        + '覆盖安装只会替换其中一份，其余旧记录仍会在卸载时生效（可能包含「卸载时删除整个安装目录」）。'
        + #13#10 + '建议改用新的安装目录，或先手动清理这些旧卸载日志后再安装。',
        '检测到多份历史卸载日志，安装已取消。请更换安装目录，'
        + '或清理旧卸载日志（unins*.dat）后重试。');
    Exit;
  end;
  Log('A08: 目标目录仅有一份本脚本身份的卸载日志，允许 overwrite 直接覆盖: ' + Base);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Msg: String;
begin
  if not IsWindowsVersionOk then
  begin
    Msg := '本程序需要 Windows 10 2004 (Build 19041) 或更高版本。' + #13#10 +
           '您当前的系统版本过低，无法运行本程序。' + #13#10#13#10 +
           '请先更新 Windows 系统后再安装。';
    MsgBox(Msg, mbCriticalError, MB_OK);
    Result := '系统版本不满足要求，安装已取消。';
    Exit;
  end;

  // [A08] 覆盖升级前的历史卸载日志身份检查（见上面的说明）
  Result := TubaCheckUninstallLogs(ExpandConstant('{app}'));
end;

// === [A08] 卸载：用户数据删除（默认保留；静默卸载自动取默认"否"） ===
// 第三批复核要求（安装器路径安全）+ 第三次复核缺口①②：
//  1) 不再递归删除安装目录：程序文件由 Inno 的「已安装文件清单」移除；
//  2) 标记文件里的路径先解析成「规范绝对路径」（统一分隔符、消解 . 与 .. 段、相对路径按
//     安装目录解析、UNC/盘符/占位符写法都处理），再判边界：盘根、UNC 共享根、安装目录本身、
//     安装目录的上级、系统与共享目录 → 一律跳过删除（保守保留）；
//  3) 【缺口①】自定义目录（标记文件里的 Custom:<路径>）一律【保守保留全部内容】：
//     目录名（含 tuba 字样也一样）、标记文件、文件名名单都不是归属证明——标记只能说明程序
//     曾把数据写到这里，不能证明这个目录连同里面的其它内容归本产品。因此不删除其中任何条目、
//     也不移除目录本身，并且不再维护「产品独有名字 / 通用名字」名单（没有可信归属记录就不删）；
//  4) 【缺口②】删除前逐级检查路径链：从盘符根/共享根到目标（含根与目标自身）任何一级是
//     重解析点（符号链接 / 目录联接 / 挂载卷）→ 该路径经由链接 → 拒绝删除。只检查最终条目不够：
//     祖先 junction 会把删除带到链接目标里去（如 Custom:D:\Alias → D:\Shared）；
//  5) 解析规则与应用的 ConfigManager / PathResolver 保持一致；无法确定 → 跳过删除。
var
  UninstallDeleteData: Boolean;
  UninstallCustomDataDir: String;  // 标记文件解析出的自定义数据目录（规范小写绝对路径；本批起仅用于记录）

// ---------- 基础字符串工具 ----------
function TubaPosCI(const Sub, S: String): Integer;
begin
  // 大小写不敏感查找（ConfigManager 判断 "Custom:" 前缀用的是 OrdinalIgnoreCase）
  Result := Pos(LowerCase(Sub), LowerCase(S));
end;

function TubaCountChar(const S, Ch: String): Integer;
var
  I: Integer;
begin
  Result := 0;
  for I := 1 to Length(S) do
    if Copy(S, I, 1) = Ch then
      Result := Result + 1;
end;

// P 等于 Base 本身，或位于 Base 之下（两边都是规范小写路径；Base 允许是盘符 'c:'）
function TubaSameOrUnder(const P, Base: String): Boolean;
begin
  Result := (Base <> '') and ((P = Base) or (Copy(P, 1, Length(Base) + 1) = Base + '\'));
end;

// ---------- 路径规范化 ----------
// 处理一个路径段：'' 与 '.' 跳过；'..' 回退一段（回退越过根 → 失败，不确定）；
// 段名含通配符、未展开的 {..}、冒号（数据流/盘符相对）、双引号等 → 失败。
procedure TubaApplySegment(const Seg: String; var Acc: String; var Ok: Boolean);
var
  I, P: Integer;
  C: String;
begin
  if not Ok then Exit;
  if (Seg = '') or (Seg = '.') then Exit;
  if Seg = '..' then
  begin
    if Acc = '' then
    begin
      Ok := False;
      Exit;
    end;
    P := 0;
    for I := 1 to Length(Acc) do
      if Acc[I] = '\' then P := I;
    if P <= 0 then
      Acc := ''
    else
      Acc := Copy(Acc, 1, P - 1);
    Exit;
  end;
  for I := 1 to Length(Seg) do
  begin
    C := Copy(Seg, I, 1);
    if (C = '*') or (C = '?') or (C = '{') or (C = '}') or (C = ':') or
       (C = '"') or (C = '<') or (C = '>') or (C = '|') then
    begin
      Ok := False;
      Exit;
    end;
  end;
  Acc := Acc + '\' + LowerCase(Seg);
end;

// 安装目录 {app} 的规范小写形式；不是普通盘符绝对路径时返回 ''
function TubaCanonAppDir: String;
var
  S, Seg, Acc, C: String;
  I: Integer;
  Ok: Boolean;
begin
  Result := '';
  S := Trim(ExpandConstant('{app}'));
  if Length(S) < 3 then Exit;
  if S[2] <> ':' then Exit;
  if (S[3] <> '\') and (S[3] <> '/') then Exit;
  Acc := '';
  Ok := True;
  Seg := '';
  for I := 4 to Length(S) do
  begin
    C := Copy(S, I, 1);
    if (C = '\') or (C = '/') then
    begin
      TubaApplySegment(Seg, Acc, Ok);
      Seg := '';
      if not Ok then Exit;
    end
    else
      Seg := Seg + C;
  end;
  if Ok then TubaApplySegment(Seg, Acc, Ok);
  if not Ok then Exit;
  Result := LowerCase(Copy(S, 1, 2)) + Acc;
end;

// 把 S 里出现的 Token（大小写不敏感）整体替换成 Value；替换值不参与后续匹配（不会死循环）
function TubaReplaceToken(const S, Token, Value: String): String;
var
  I, L, N: Integer;
begin
  Result := '';
  L := Length(Token);
  if L = 0 then
  begin
    Result := S;
    Exit;
  end;
  N := Length(S);
  I := 1;
  while I <= N do
  begin
    if (I + L - 1 <= N) and (LowerCase(Copy(S, I, L)) = LowerCase(Token)) then
    begin
      Result := Result + Value;
      I := I + L;
    end
    else
    begin
      Result := Result + Copy(S, I, 1);
      I := I + 1;
    end;
  end;
end;

// 上一级目录（'c:\a\b' → 'c:\a'；'c:\a' → 'c:'；其它 → ''）
function TubaParentDirOf(const Dir: String): String;
var
  I, P: Integer;
begin
  Result := '';
  P := 0;
  for I := 1 to Length(Dir) do
    if Dir[I] = '\' then P := I;
  if P > 1 then Result := Copy(Dir, 1, P - 1);
end;

// 与 PathResolver.ExpandPath 对齐：只展开三个能确定的占位符。
// {DataDir}（自引用）与 {ToolsRoot}（运行时可被用户改到别处）不展开 → 残留的 {..} 会被
// TubaApplySegment 拒绝 → 跳过删除（保守保留）。
function TubaExpandKnownPlaceholders(const S: String): String;
var
  AppD, ParentD: String;
begin
  AppD := Trim(ExpandConstant('{app}'));
  ParentD := TubaParentDirOf(AppD);
  Result := S;
  Result := TubaReplaceToken(Result, '{AppDir}', AppD);
  if ParentD <> '' then
    Result := TubaReplaceToken(Result, '{ParentDir}', ParentD);
  Result := TubaReplaceToken(Result, '{AppDataDir}',
    Trim(ExpandConstant('{localappdata}')) + '\TubaWinUi3');
end;

// 解析 + 规范化（边界检查由 TubaIsSafeUninstallDataDir 负责）。
// 与 ConfigManager 一致：展开占位符后 Path.IsPathRooted 为真 → 绝对路径；
// 否则相对路径 → Path.Combine(安装目录, 展开值)。环境变量写法（%TEMP%\x）ConfigManager
// 不做展开（按相对安装目录处理），这里保持一致。
function TubaResolvePath(const Raw: String; var Canon: String; var Why: String): Boolean;
var
  S, Rest, Seg, Acc, C: String;
  I, J: Integer;
  Unc, Drive, Ok: Boolean;
begin
  Result := False;
  Canon := '';
  Why := '';
  S := Trim(TubaExpandKnownPlaceholders(Trim(Raw)));
  if S = '' then
  begin
    Why := '空路径';
    Exit;
  end;

  // 设备路径（\\?\、\\.\）不做路径运算
  if Length(S) >= 3 then
  begin
    if (Copy(S, 1, 2) = '\\') and ((S[3] = '?') or (S[3] = '.')) then
    begin
      Why := '设备路径不解析';
      Exit;
    end;
  end;

  Unc := False;
  Drive := False;
  if Length(S) >= 2 then
  begin
    if ((S[1] = '\') or (S[1] = '/')) and ((S[2] = '\') or (S[2] = '/')) then
      Unc := True;
    if (not Unc) and (S[2] = ':') then
    begin
      if Length(S) = 2 then
        Drive := True
      else if (S[3] = '\') or (S[3] = '/') then
        Drive := True;
    end;
  end;
  if (not Unc) and (not Drive) and (Length(S) >= 2) and (S[2] = ':') then
  begin
    Why := '盘符相对路径（依赖各驱动器的当前目录，不确定）';
    Exit;
  end;

  Acc := '';
  if Drive then
  begin
    Canon := LowerCase(Copy(S, 1, 2));       // 'c:'
    Rest := Copy(S, 3, MaxInt);              // '\a\b'
  end
  else if Unc then
  begin
    // \\server\share\...：server 与 share 视为不可回退的根
    Rest := Copy(S, 3, MaxInt);
    Seg := '';
    J := 0;
    for I := 1 to Length(Rest) do
    begin
      C := Copy(Rest, I, 1);
      if (C = '\') or (C = '/') then
      begin
        if Seg <> '' then
        begin
          if J = 0 then
            Canon := '\\' + LowerCase(Seg)
          else if J = 1 then
            Canon := Canon + '\' + LowerCase(Seg);
          J := J + 1;
          Seg := '';
          if J >= 2 then
          begin
            Rest := Copy(Rest, I + 1, MaxInt);
            Break;
          end;
        end;
      end
      else
        Seg := Seg + C;
    end;
    if (J = 1) and (Seg <> '') then
    begin
      Canon := Canon + '\' + LowerCase(Seg);  // '\\server\share' 结尾没有分隔符
      Rest := '';
      J := 2;
    end;
    if (J < 2) or (Canon = '') then
    begin
      Why := 'UNC 路径缺少 server/share';
      Exit;
    end;
  end
  else if (S[1] = '\') or (S[1] = '/') then
  begin
    Why := '无盘符的根相对路径（依赖当前驱动器，不确定）';
    Exit;
  end
  else
  begin
    // 相对路径：与 ConfigManager 一致，以安装目录为基准（Path.Combine(AppDirectory, 展开值)）
    Acc := TubaCanonAppDir();
    if Acc = '' then
    begin
      Why := '安装目录无法确定';
      Exit;
    end;
    Canon := Copy(Acc, 1, 2);                 // 盘符
    Acc := Copy(Acc, 3, MaxInt);              // '\...'
    Rest := S;
  end;

  // 逐段消解 . 与 ..，并拒绝非法字符
  Ok := True;
  Seg := '';
  for I := 1 to Length(Rest) do
  begin
    C := Copy(Rest, I, 1);
    if (C = '\') or (C = '/') then
    begin
      TubaApplySegment(Seg, Acc, Ok);
      Seg := '';
      if not Ok then Break;
    end
    else
      Seg := Seg + C;
  end;
  if Ok then TubaApplySegment(Seg, Acc, Ok);
  if not Ok then
  begin
    Why := '路径段含非法字符，或 .. 越出根（不确定）';
    Exit;
  end;

  Canon := Canon + Acc;
  Result := True;
end;

// 常量路径 → 规范小写绝对路径（解析失败返回 ''）
function TubaCanonConstant(const ConstantName: String): String;
var
  C, Why: String;
begin
  Result := '';
  if TubaResolvePath(ExpandConstant(ConstantName), C, Why) then
    Result := C;
end;

// 系统/共享目录保护（第三批复核："共享目录、无法确认归属的内容保守保留"）：
//  规则 1：Canon 就是某个系统/用户共享根，或位于它们之上 → 跳过删除；
//  规则 2：Canon 落在关键系统目录之内（安装目录 {app} 及其子目录除外）→ 跳过删除。
// 注意：不把 {sd}（系统盘根）放进名单——盘根的“之下”包含全部路径，会把一切合法目标都拦掉；
// 盘根本身已由 TubaIsSafeUninstallDataDir 的盘根检查拒绝。
function TubaIsProtectedUninstallPath(const Canon: String; var Why: String): Boolean;
var
  Names, Name, W, AppCanon: String;
  I: Integer;
begin
  Result := False;
  AppCanon := TubaCanonAppDir();

  Names := 'win,sys,pf,pf32,localappdata,userappdata,userprofile,userdocs,commondocs,commonappdata,commonprograms';
  Name := '';
  for I := 1 to Length(Names) + 1 do
  begin
    if (I > Length(Names)) or (Copy(Names, I, 1) = ',') then
    begin
      if Name <> '' then
      begin
        W := TubaCanonConstant('{' + Name + '}');
        if (W <> '') and TubaSameOrUnder(W, Canon) then
        begin
          Why := '系统或共享目录: {' + Name + '}';
          Result := True;
          Exit;
        end;
      end;
      Name := '';
    end
    else
      Name := Name + Copy(Names, I, 1);
  end;

  if TubaSameOrUnder(Canon, AppCanon) then Exit;   // 安装目录内不按关键目录处理

  Names := 'win,sys,pf,pf32,commonappdata';
  Name := '';
  for I := 1 to Length(Names) + 1 do
  begin
    if (I > Length(Names)) or (Copy(Names, I, 1) = ',') then
    begin
      if Name <> '' then
      begin
        W := TubaCanonConstant('{' + Name + '}');
        if (W <> '') and TubaSameOrUnder(Canon, W) then
        begin
          Why := '关键系统目录之内: {' + Name + '}';
          Result := True;
          Exit;
        end;
      end;
      Name := '';
    end
    else
      Name := Name + Copy(Names, I, 1);
  end;
end;

// 删除前的边界检查：盘根、UNC 共享根、安装目录本身/上级、系统与共享目录都要拒绝
function TubaIsSafeUninstallDataDir(const Canon: String; var Why: String): Boolean;
var
  AppCanon: String;
begin
  Result := False;
  if Canon = '' then
  begin
    Why := '空路径';
    Exit;
  end;
  if Copy(Canon, 1, 2) = '\\' then
  begin
    if TubaCountChar(Canon, '\') < 4 then       // \\server\share 本身也是共享根
    begin
      Why := 'UNC 共享根';
      Exit;
    end;
  end
  else
  begin
    if (Length(Canon) <= 2) or (TubaCountChar(Canon, '\') = 0) then
    begin
      Why := '盘根';
      Exit;
    end;
  end;
  AppCanon := TubaCanonAppDir();
  if AppCanon <> '' then
  begin
    if Canon = AppCanon then
    begin
      Why := '安装目录本身';
      Exit;
    end;
    if Copy(AppCanon, 1, Length(Canon) + 1) = Canon + '\' then
    begin
      Why := '安装目录的上级目录';
      Exit;
    end;
  end;
  if TubaIsProtectedUninstallPath(Canon, Why) then Exit;
  Result := True;
end;

// P 是否严格位于安装目录之内
function TubaIsStrictlyInsideAppDir(const P: String): Boolean;
var
  C, A, Why: String;
begin
  Result := False;
  if not TubaResolvePath(P, C, Why) then Exit;
  A := TubaCanonAppDir();
  if A = '' then Exit;
  Result := Copy(C, 1, Length(A) + 1) = A + '\';
end;

// 重解析点（符号链接 / 目录联接 / 挂载卷）：返回 True 也包含「查询失败 / 无法确认」的情形
function TubaIsReparsePoint(const P: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := True;                                  // 查询失败 → 不可确认 → 当作链接处理
  if P = '' then Exit;
  if FindFirst(P, FindRec) then
  begin
    Result := (FindRec.Attributes and 1024) <> 0;  // 1024 = FILE_ATTRIBUTE_REPARSE_POINT
    FindClose(FindRec);
  end;
end;

// 【缺口②】从根到目标逐级检查（含盘符根/共享根与目标自身）：
// 任何一级是重解析点（或属性无法确认）→ 这条路径经由链接 → 拒绝删除；返回 True 时 Where 给出那一级。
function TubaAnyReparseInPath(const Canon: String; var Where: String): Boolean;
var
  I: Integer;
  Prefix, Norm: String;
begin
  Result := True;                                  // 参数异常 → 视为不可删除
  Where := '';
  if Canon = '' then Exit;
  for I := 1 to Length(Canon) do
  begin
    if (Canon[I] = '\') or (I = Length(Canon)) then
    begin
      Prefix := Copy(Canon, 1, I);
      Norm := Prefix;                              // 去掉尾部分隔符，用于判断 UNC 深度
      while (Length(Norm) > 1) and (Norm[Length(Norm)] = '\') do
        Norm := Copy(Norm, 1, Length(Norm) - 1);
      // 只探测「文件系统实体」前缀：
      //   · 盘符根（'c:\'）必须检查（要求里明确「含根」）；
      //   · UNC 的 '\'、'\\'、'\\server\'、'\\server\share'（共享根本身）不探测：那是网络访问，
      //     且共享根已由边界检查拒绝；比共享根更深的前缀照常探测。
      if (Length(Prefix) >= 3) and
         ((Copy(Prefix, 1, 2) <> '\\') or (TubaCountChar(Norm, '\') >= 4)) then
      begin
        if TubaIsReparsePoint(Prefix) then
        begin
          Where := Prefix;
          Exit;
        end;
      end;
    end;
  end;
  Result := False;
end;

// 删除前的最后一道门（缺口①②）：目标存在 → 规范路径 → 边界检查 → 整条路径链无重解析点
function TubaReadyToDeleteDir(const Dir: String; var Canon: String; var Why: String): Boolean;
begin
  Result := False;
  Canon := '';
  Why := '';
  if not DirExists(Dir) then Exit;
  if not TubaResolvePath(Dir, Canon, Why) then Exit;
  if not TubaIsSafeUninstallDataDir(Canon, Why) then Exit;
  if TubaAnyReparseInPath(Canon, Why) then
  begin
    Why := '路径链上存在重解析点（符号链接/目录联接/挂载卷），拒绝删除: ' + Why;
    Exit;
  end;
  Result := True;
end;

procedure TubaAskDeleteUserData;
var
  MarkerPath: String;
  MarkerContent: AnsiString;
  CustomPos: Integer;
  CustomDir, Canon, Why: String;
begin
  UninstallDeleteData := False;
  UninstallCustomDataDir := '';

  if DirExists(ExpandConstant('{localappdata}\TubaWinUi3')) or
     FileExists(ExpandConstant('{app}\Data\.config_location')) then
  begin
    if SuppressibleMsgBox('是否同时删除本软件的用户数据？' + #13#10 + #13#10 +
              '删除内容包括：' + #13#10 +
              '· 设置与配置' + #13#10 +
              '· 收藏与自定义工具信息' + #13#10 +
              '· AI 助手聊天记录与记忆' + #13#10 +
              '· 图标缓存、WebView2 缓存等' + #13#10 + #13#10 +
              '默认数据位于 ' + ExpandConstant('{localappdata}\TubaWinUi3') + '；' + #13#10 +
              '若数据位于安装目录内的 Data 子目录，会一并删除。' + #13#10 +
              '若数据位于您指定的自定义目录：该目录内的内容会完整保留（本程序不做删除）。' + #13#10 +
              '此操作不可恢复。' + #13#10 + #13#10 +
              '选择"是"删除用户数据，选择"否"仅卸载程序、保留数据。',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    begin
      UninstallDeleteData := True;

      // 数据位置标记：{app}\Data\.config_location（"AppRoot"=安装目录内；"Custom:<路径>"=自定义目录）
      MarkerPath := ExpandConstant('{app}\Data\.config_location');
      if FileExists(MarkerPath) and LoadStringFromFile(MarkerPath, MarkerContent) then
      begin
        MarkerContent := Trim(Utf8Decode(MarkerContent));
        CustomPos := TubaPosCI('Custom:', MarkerContent);
        if CustomPos > 0 then
        begin
          CustomDir := Trim(Copy(MarkerContent, CustomPos + 7, MaxInt));
          // 不剥离开头的分隔符：Path.IsPathRooted("\foo") 为真（相对当前驱动器），
          // 这种写法无法确定，交给解析器拒绝，绝不改写成 {app}\foo。
          // 解析与边界检查只用于记录：本批起自定义目录一律完整保留（见 TubaDeleteUserData）。
          if TubaResolvePath(CustomDir, Canon, Why) then
          begin
            if TubaIsSafeUninstallDataDir(Canon, Why) then
              UninstallCustomDataDir := Canon
            else
              Log('A08: 自定义数据目录未通过边界检查（本批起一律完整保留）: ' + Canon + ' (' + Why + ')');
          end
          else
            Log('A08: 自定义数据目录无法解析（一律完整保留）: ' + CustomDir + ' (' + Why + ')');
        end;
      end;
    end;
  end;
end;

procedure TubaDeleteUserData;
var
  Dir, Canon, Why: String;
begin
  // 1) 默认数据目录：%LocalAppData%\TubaWinUi3（安装器固定路径）
  Dir := ExpandConstant('{localappdata}\TubaWinUi3');
  if not DirExists(Dir) then
    Log('A08: 默认数据目录不存在，无需删除: ' + Dir)
  else if TubaReadyToDeleteDir(Dir, Canon, Why) then
  begin
    if not DelTree(Dir, True, True, True) then
      Log('A08: 未能完整删除默认数据目录: ' + Dir);
  end
  else
    Log('A08: 默认数据目录未通过删除前检查，跳过删除: ' + Dir + ' (' + Why + ')');

  // 2) 安装目录内的数据目录：{app}\Data（AppRoot 模式；容器在安装目录内，归本产品）
  Dir := ExpandConstant('{app}\Data');
  if not TubaIsStrictlyInsideAppDir(Dir) then
    Log('A08: {app}\Data 不在安装目录内（或无法确定），保守保留: ' + Dir)
  else if TubaReadyToDeleteDir(Dir, Canon, Why) then
  begin
    if not DelTree(Dir, True, True, True) then
      Log('A08: 未能完整删除安装目录内的数据目录: ' + Dir);
  end
  else
    Log('A08: {app}\Data 未通过删除前检查，跳过删除: ' + Dir + ' (' + Why + ')');

  // 3) 自定义数据目录（标记文件里的 Custom:<路径>）：【缺口①】保守保留全部内容 ——
  //    目录名、标记文件、文件名名单都不是归属证明。这里只做体检与记录，
  //    绝不删除其中任何内容、也不移除目录本身（目录非空/为空都保留）。
  if UninstallCustomDataDir = '' then Exit;
  if not TubaResolvePath(UninstallCustomDataDir, Canon, Why) then
  begin
    Log('A08: 自定义数据目录无法解析，完整保留: ' + UninstallCustomDataDir + ' (' + Why + ')');
    Exit;
  end;
  if TubaAnyReparseInPath(Canon, Why) then
  begin
    Log('A08: 自定义数据目录经由重解析点，完整保留: ' + Canon + ' (' + Why + ')');
    Exit;
  end;
  if not TubaIsSafeUninstallDataDir(Canon, Why) then
    Log('A08: 自定义数据目录未通过边界检查（本就完整保留）: ' + Canon + ' (' + Why + ')')
  else
    Log('A08: 自定义数据目录完整保留（不删除任何内容）: ' + Canon);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    TubaAskDeleteUserData
  else if CurUninstallStep = usPostUninstall then
  begin
    if UninstallDeleteData then
      TubaDeleteUserData;
  end;
end;
