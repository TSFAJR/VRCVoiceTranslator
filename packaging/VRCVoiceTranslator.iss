#ifndef PayloadDir
  #error PayloadDir must point to the self-contained win-x64 publish directory.
#endif
#ifndef AppVersion
  #error AppVersion is required. Use Build-Packages.ps1 to pass the project version.
#endif

[Setup]
AppId={{FB175A1D-1EB4-472B-A93E-3BF74727F9BC}
AppName=VRC Voice Translator
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\VRCVoiceTranslator
DisableProgramGroupPage=yes
AllowNoIcons=yes
UninstallDisplayIcon={app}\VRCVoiceTranslator.exe
SetupIconFile=..\src\App\AppIcon.ico
OutputBaseFilename=VRCVoiceTranslator-Setup-win-x64
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
AppMutex=Local\VRCVoiceTranslator
SetupMutex=VRCVoiceTranslatorSetup
CloseApplications=no
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[CustomMessages]
DesktopShortcut=创建桌面图标
UninstallOptionsTitle=VRChat 语音翻译 · 卸载选项
UninstallOptionsDescription=勾选后将清除本机保存的设置和 API Key。
DeleteConfiguration=删除所有配置文件
UninstallContinue=继续卸载
UninstallCancel=取消
ConfigDeleteFailed=无法删除配置文件：%1

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\VRC Voice Translator"; Filename: "{app}\VRCVoiceTranslator.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\VRC Voice Translator"; Filename: "{app}\VRCVoiceTranslator.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Code]
var
  DeleteConfiguration: Boolean;

function InitializeUninstall(): Boolean;
var
  Dialog: TSetupForm;
  DeleteCheck: TNewCheckBox;
  Description: TNewStaticText;
  ContinueButton, CancelButton: TNewButton;
begin
  Result := True;
  DeleteConfiguration := False;
  if UninstallSilent() then
    Exit;

  Dialog := CreateCustomForm(450, 140, True, True);
  try
    Dialog.Caption := CustomMessage('UninstallOptionsTitle');
    Dialog.Position := poScreenCenter;
    Dialog.BorderStyle := bsDialog;

    DeleteCheck := TNewCheckBox.Create(Dialog);
    DeleteCheck.Parent := Dialog;
    DeleteCheck.Caption := CustomMessage('DeleteConfiguration');
    DeleteCheck.Checked := False;
    DeleteCheck.SetBounds(ScaleX(18), ScaleY(20), Dialog.ClientWidth - ScaleX(36), ScaleY(24));

    Description := TNewStaticText.Create(Dialog);
    Description.Parent := Dialog;
    Description.Caption := CustomMessage('UninstallOptionsDescription');
    Description.AutoSize := False;
    Description.WordWrap := True;
    Description.SetBounds(ScaleX(38), ScaleY(52), Dialog.ClientWidth - ScaleX(56), ScaleY(36));

    ContinueButton := TNewButton.Create(Dialog);
    ContinueButton.Parent := Dialog;
    ContinueButton.Caption := CustomMessage('UninstallContinue');
    ContinueButton.SetBounds(Dialog.ClientWidth - ScaleX(226), Dialog.ClientHeight - ScaleY(49), ScaleX(102), ScaleY(30));
    ContinueButton.ModalResult := mrOk;
    ContinueButton.Default := True;

    CancelButton := TNewButton.Create(Dialog);
    CancelButton.Parent := Dialog;
    CancelButton.Caption := CustomMessage('UninstallCancel');
    CancelButton.SetBounds(Dialog.ClientWidth - ScaleX(112), Dialog.ClientHeight - ScaleY(49), ScaleX(94), ScaleY(30));
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;

    Result := Dialog.ShowModal() = mrOk;
    DeleteConfiguration := Result and DeleteCheck.Checked;
  finally
    Dialog.Free();
  end;
end;

procedure DeleteSettingsFile(const Path: String);
begin
  if FileExists(Path) and not DeleteFile(Path) then
    SuppressibleMsgBox(FmtMessage(CustomMessage('ConfigDeleteFailed'), [Path]), mbError, MB_OK, IDOK);
end;

function IsTemporaryConfig(const Name: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  if Length(Name) <> Length('config.json.') + 32 + Length('.tmp') then
    Exit;
  for Index := Length('config.json.') + 1 to Length('config.json.') + 32 do
    if Pos(Name[Index], '0123456789abcdefABCDEF') = 0 then
      Exit;
  Result := True;
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
var
  ConfigDir: String;
  Temp: TFindRec;
begin
  if (Step <> usPostUninstall) or not DeleteConfiguration then
    Exit;

  ConfigDir := ExpandConstant('{localappdata}\VRCVoiceTranslator\');
  DeleteSettingsFile(ConfigDir + 'config.json');
  if FindFirst(ConfigDir + 'config.json.*.tmp', Temp) then
  begin
    try
      repeat
        if (Temp.Attributes and FILE_ATTRIBUTE_DIRECTORY = 0) and IsTemporaryConfig(Temp.Name) then
          DeleteSettingsFile(ConfigDir + Temp.Name);
      until not FindNext(Temp);
    finally
      FindClose(Temp);
    end;
  end;
end;
