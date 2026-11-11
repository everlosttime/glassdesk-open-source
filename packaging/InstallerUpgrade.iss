{ Shared wizard flow for the user-scope GlassDesk installers. }
var
  ExistingInstallDirectory: String;
  CustomInstallDirectory: String;
  UpgradeChoicePage: TWizardPage;
  OverwriteRadio: TNewRadioButton;
  CustomDirectoryRadio: TNewRadioButton;

function RegisteredInstallDirectory(RootKey: Integer): String;
var
  Key, Directory: String;
begin
  Result := '';
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdValue}_is1';
  if RegQueryStringValue(RootKey, Key, 'InstallLocation', Directory) then
    if FileExists(AddBackslash(Directory) + 'GlassDesk.exe') then
    begin
      Result := RemoveBackslashUnlessRoot(Directory);
      exit;
    end;
  { Earlier Inno installers may only have the built-in path value. }
  if RegQueryStringValue(RootKey, Key, 'Inno Setup: App Path', Directory) then
    if FileExists(AddBackslash(Directory) + 'GlassDesk.exe') then
      Result := RemoveBackslashUnlessRoot(Directory);
end;

procedure AddUpgradeDescription(const Text: String; Top, Height: Integer);
var
  LabelControl: TNewStaticText;
begin
  LabelControl := TNewStaticText.Create(UpgradeChoicePage);
  LabelControl.Parent := UpgradeChoicePage.Surface;
  LabelControl.AutoSize := False;
  LabelControl.WordWrap := True;
  LabelControl.SetBounds(0, ScaleY(Top), UpgradeChoicePage.SurfaceWidth, ScaleY(Height));
  LabelControl.Caption := Text;
end;

procedure InitializeWizard;
begin
  { Match the current user's installation; never another user's copy. }
  ExistingInstallDirectory := RegisteredInstallDirectory(HKCU64);
  if ExistingInstallDirectory = '' then
    ExistingInstallDirectory := RegisteredInstallDirectory(HKCU32);
  if ExistingInstallDirectory = '' then exit;

  CustomInstallDirectory := ExpandConstant('{localappdata}\Programs\GlassDesk');
  if CompareText(RemoveBackslashUnlessRoot(CustomInstallDirectory), ExistingInstallDirectory) = 0 then
    CustomInstallDirectory := CustomInstallDirectory + '-New';

  UpgradeChoicePage := CreateCustomPage(wpWelcome, '选择安装方式',
    '已检测到本机安装的 GlassDesk，请选择如何安装。');
  AddUpgradeDescription('当前安装位置：' + #13#10 + ExistingInstallDirectory, 0, 60);

  OverwriteRadio := TNewRadioButton.Create(UpgradeChoicePage);
  OverwriteRadio.Parent := UpgradeChoicePage.Surface;
  OverwriteRadio.SetBounds(0, ScaleY(72), UpgradeChoicePage.SurfaceWidth, ScaleY(22));
  OverwriteRadio.Caption := '覆盖升级（推荐）';
  OverwriteRadio.Checked := True;
  AddUpgradeDescription('自动安装到原位置，保留已有设置、应用列表和布局。', 100, 36);

  CustomDirectoryRadio := TNewRadioButton.Create(UpgradeChoicePage);
  CustomDirectoryRadio.Parent := UpgradeChoicePage.Surface;
  CustomDirectoryRadio.SetBounds(0, ScaleY(148), UpgradeChoicePage.SurfaceWidth, ScaleY(22));
  CustomDirectoryRadio.Caption := '自定义安装位置';
  AddUpgradeDescription('下一步选择其他文件夹。旧目录和原有数据会保留，不会自动迁移。', 176, 48);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if UpgradeChoicePage = nil then exit;
  if PageID = UpgradeChoicePage.ID then
    Result := WizardSilent
  else if PageID = wpSelectDir then
    Result := (not WizardSilent) and OverwriteRadio.Checked;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (UpgradeChoicePage = nil) or WizardSilent then exit;
  if CurPageID = UpgradeChoicePage.ID then
  begin
    if OverwriteRadio.Checked then
      WizardForm.DirEdit.Text := ExistingInstallDirectory
    else
      WizardForm.DirEdit.Text := CustomInstallDirectory;
  end
  else if CurPageID = wpSelectDir then
    CustomInstallDirectory := WizardDirValue;
end;

function BackButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (UpgradeChoicePage <> nil) and (CurPageID = wpSelectDir) then
    CustomInstallDirectory := WizardDirValue;
end;
