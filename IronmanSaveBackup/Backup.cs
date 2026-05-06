using IronmanSaveBackup.Properties;
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IronmanSaveBackup.Enums;

namespace IronmanSaveBackup
{


    public class Backup
    {
        public string BackupParentFolder { get; set; }
        private string _saveParentFolder;

        public string SaveParentFolder
        {
            get => _saveParentFolder;
            set
            {
                _saveParentFolder = value;

                if (_saveParentFolder.Contains("War of the Chosen"))
                {
                    SavePattern = new Regex(@"^save_IRONMAN- Campaign .*$|^save_铁人：战役.*$");
                    _saveType = SaveType.WotC;
                }
                else if (_saveParentFolder.Contains("Chimera Squad"))
                {
                    SavePattern = new Regex(@"^.*-IronMan$");
                    _saveType = SaveType.Chimera;
                }
                else
                {
                    SavePattern = new Regex(@"^save.*$");
                    _saveType = SaveType.Original;
                }
            }

        }
        public string RestoreFile { get; set; }
        public int MaxBackups { get; set; }
        public int BackupInterval { get; set; }
        public long Campaign { get; set; }
        public string RestoreName { get; set; }
        private SaveType _saveType { get; set; }
        public CancellationTokenSource CancelBackupSource { get; set; }
        private bool _backupActive;

        private Regex SavePattern { get; set; }

        public void StopBackup()
        {
            CancelBackupSource.Cancel();
            _backupActive = false; // 直接设置字段，避免触发属性setter
        }

        public bool BackupActive
        {
            get => _backupActive;
            set
            {
                if (_backupActive == value) return; // 避免重复设置

                _backupActive = value;
                if (_backupActive)
                {
                    // 重置 CancellationTokenSource
                    CancelBackupSource = new CancellationTokenSource();
                    StartBackup();
                }
            }
        }

        private DateTime? _lastUpdated;

        public bool EventDrivenBackups { get; set; }

        public DateTime? LastUpdated
        {
            get => _lastUpdated;
            set
            {
                _lastUpdated = value;
                MainWindow._MainWindow.RecentBackup = value == null ? "Backup Failed" : $"Campaign {Campaign} @ {value}";
            }
        }

        public Backup()
        {
            CancelBackupSource = new CancellationTokenSource();
        }

        ~Backup()
        {
            UpdateSettings();
        }

        public void UpdateSettings()
        {
            Settings.Default.BackupParentFolder  = BackupParentFolder;
            Settings.Default.SaveInterval        = BackupInterval;
            Settings.Default.SaveParentFolder    = SaveParentFolder;
            Settings.Default.MaxBackups          = MaxBackups;
            Settings.Default.EnableOnEventBackup = EventDrivenBackups;
            Settings.Default.LastUpdated         = LastUpdated ?? DateTime.MinValue;
            Settings.Default.MostRecentCampaign  = Campaign;
            Settings.Default.Save();
        }

        public void RestoreBackup()
        {
            RestoreName = BuildRestoreName();
            var restorePath = Path.Combine(SaveParentFolder, RestoreName);
            if (Campaign == -1)
            {
                MessageOperations.UserMessage(Resources.CampaignNotFound, MessageType.RestoreError);
            }
            try
            {
                File.Copy(RestoreFile, restorePath, true);
                MessageOperations.UserMessage(string.Format(Resources.SaveRestoredSuccess, Campaign), MessageType.RestoreSuccess);
            }
            catch (IOException)
            {
                MessageOperations.UserMessage(Resources.FileInUse, MessageType.FileInUseError);
            }
        }

        public DateTime? ForceCreateBackup()
        {
            // 添加调试信息
            System.Diagnostics.Debug.WriteLine($"SaveParentFolder: {SaveParentFolder}");
            System.Diagnostics.Debug.WriteLine($"SavePattern: {SavePattern}");

            //Grabs the most recently updated IronMan save in the save folder
            var saveDirectoryInfo = new DirectoryInfo(SaveParentFolder);
            var files         = saveDirectoryInfo.GetFiles().OrderByDescending(x => x.LastAccessTime).ToList();
            var fileName          = files.FirstOrDefault(x => SavePattern.IsMatch(Path.GetFileName(x.FullName)));

            if (fileName != null)
            {
                Campaign = GetCampaignFromFileName(fileName.Name);
                if (Campaign == -1)
                {
                    MessageOperations.UserMessage(Resources.CampaignNotFound,
                        MessageType.BackupError);
                    return null;
                }

                //Create our backup directory and file names
                var backupFileName       = BuildBackupName();
                var backupChildDirectory = BuildBackupLocation();
                var backupFullName       = Path.Combine(backupChildDirectory, backupFileName);

                try
                {
                    File.Copy(fileName.FullName, backupFullName, false);

                    //Only delete additional backups if the new backup was copied successfully
                    if (MaxBackups > 0)
                    {
                        DeleteAdditionalBackups(backupChildDirectory);
                    }
                    MessageOperations.UserMessage(
                        string.Format(Resources.BackupCreatedSuccess, Campaign),
                        MessageType.BackupSuccess);
                    return DateTime.Now;
                }
                catch (IOException)
                {
                    MessageOperations.UserMessage("", MessageType.FileInUseError);
                    return null;
                }
            }
            MessageOperations.UserMessage(Resources.NoIronmanSaves,
                MessageType.BackupError);
            return null;
        }

        private void DeleteAdditionalBackups(string backupChildDirectory)
        {
            var backupChildInfo = new DirectoryInfo(backupChildDirectory);
            var backupFiles     = backupChildInfo.GetFiles().OrderBy(x => x.CreationTime).ToList();
            var numToDelete     = backupFiles.Count - MaxBackups;
            if (numToDelete <= 0) return;
            foreach (var file in backupFiles.Take(numToDelete).ToList())
            {
                File.Delete(file.FullName);
            }
        }

        private long GetCampaignFromFileName(string fileName)
        {
            Regex regex;
            switch (_saveType)
            {
                case SaveType.Original:
                    regex = new Regex(@"^save(.*)$");
                    break;
                case SaveType.WotC:
                    regex = new Regex(@"^save_铁人：战役(\d+)$");
                    break;
                case SaveType.Chimera:
                    regex = new Regex(@"^.*?(?=-IronMan)");
                    break;
                default:
                    return -1;
            } 
            
            var match    = regex.Match(fileName);
            if (_saveType == SaveType.WotC && match.Success && match.Groups.Count > 1)
            {
                if (long.TryParse(match.Groups[1].Value, out long idValue))
                {
                    return idValue;
                }
            }
            else if (_saveType == SaveType.Chimera)
            {
                if (long.TryParse(match.Value, out long idValue))
                {
                    return idValue;
                }
            }
            else if (match.Success && match.Groups.Count > 1)
            {
                if (long.TryParse(match.Groups[1].Value, out long idValue))
                {
                    return idValue;
                }
            }

            return -1;
        }

        private long GetCampaignFromBackup()
        {
            var idString = Directory.GetParent(RestoreFile).Name;
            long idValue;
            if (long.TryParse(idString, out idValue))
            {
                return idValue;
            }
            return -1;
        }

        private string BuildRestoreName()
        {
            Campaign = GetCampaignFromBackup();
            switch (_saveType)
            {
                case SaveType.Original:
                    return string.Format(Resources.SaveRestoreNameVanilla, Campaign);
                case SaveType.WotC:
                    return string.Format("save_铁人：战役{0}", Campaign);
                case SaveType.Chimera:
                    return string.Format(Resources.SaveRestoreNameChimera, Campaign);
                default:
                    return string.Format(Campaign.ToString());
            }
        }

        private string BuildBackupLocation()
        {
            string childDirectory;

            if (SaveParentFolder.Contains("Enemy Unknown"))
            {
                childDirectory = Path.Combine(BackupParentFolder,"XEU", Campaign.ToString());
            }
            else if (SaveParentFolder.Contains("Enemy Within"))
            {
                childDirectory = Path.Combine(BackupParentFolder, "XEW", Campaign.ToString());
            }
            else if (SaveParentFolder.Contains("War of the Chosen"))
            {
                childDirectory = Path.Combine(BackupParentFolder, "WotC", Campaign.ToString());
            }
            else if (SaveParentFolder.Contains("Chimera Squad"))
            {
                childDirectory = Path.Combine(BackupParentFolder, "Chimera Squad", Campaign.ToString());
            }
            else
            {
                childDirectory = Path.Combine(BackupParentFolder, "X2", Campaign.ToString());
            }

            if (Directory.Exists(childDirectory))
            {
                return childDirectory;
            }

            Directory.CreateDirectory(childDirectory);
            return childDirectory;
        }

        private static string BuildBackupName()
        {
            return $@"{DateTime.Now:yyyy-dd-MM-HH-mm-ss}.isb";
        }

        private async void StartBackup()
        {
            var cancellationToken = CancelBackupSource.Token;
            try
            {
                if (EventDrivenBackups)
                {
                    // 事件驱动模式
                    await Task.Run(() => EventBackup(cancellationToken), cancellationToken);
                }
                else
                {
                    var interval = TimeSpan.FromMinutes(BackupInterval);
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        // 确保UI更新在主线程
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            LastUpdated = CreateBackup();
                        });

                        try
                        {
                            await Task.Delay(interval, cancellationToken);
                        }
                        catch (TaskCanceledException)
                        {
                            // 取消时正常退出循环
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 任务被取消，正常处理
                System.Diagnostics.Debug.WriteLine("备份任务已取消");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"备份任务发生错误: {ex.Message}");
            }
            finally
            {
                // 确保在退出时重置状态
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _backupActive = false; // 直接设置字段，避免触发StartBackup
                    MainWindow._MainWindow.SaveTextbox.IsEnabled = true;
                    MainWindow._MainWindow.BackupTextbox.IsEnabled = true;
                    MainWindow._MainWindow.StartButton.IsEnabled = true;
                    MainWindow._MainWindow.StopButton.IsEnabled = false;
                });
            }
        }

        private async Task EventBackup(CancellationToken cancellationToken)
        {
            using (var watcher = new FileSystemWatcher())
            {
                watcher.Path = SaveParentFolder;
                watcher.NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.LastAccess |
                                       NotifyFilters.LastWrite | NotifyFilters.Attributes | NotifyFilters.Size;

                switch (_saveType)
                {
                    case SaveType.Original:
                        watcher.Filter = "save";
                        break;
                    case SaveType.WotC:
                        watcher.Filter = "save_铁人：*";
                        break;
                    case SaveType.Chimera:
                        watcher.Filter = "*IronMan*";
                        break;
                    default:
                        watcher.Filter = "*IronMan*";
                        break;
                }

                watcher.Changed += OnEvent;
                watcher.EnableRaisingEvents = true;

                // 使用 TaskCompletionSource 等待取消
                var tcs = new TaskCompletionSource<bool>();
                using (cancellationToken.Register(() => tcs.TrySetResult(true)))
                {
                    await tcs.Task; // 异步等待取消信号
                }

                watcher.EnableRaisingEvents = false; // 停止监控
            }
        }

        private void OnEvent(object sender, FileSystemEventArgs e)
        {
            // 确保在正确的线程上执行
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LastUpdated = CreateBackup();
            });
        }

        private DateTime? CreateBackup()
        {
            try
            {
                var saveDirectoryInfo = new DirectoryInfo(SaveParentFolder);
                if (!saveDirectoryInfo.Exists)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageOperations.UserMessage("存档文件夹不存在", MessageType.BackupError);
                    });
                    return null;
                }

                var files = saveDirectoryInfo.GetFiles().OrderByDescending(x => x.LastAccessTime).ToList();
                var fileName = files.FirstOrDefault(x => SavePattern.IsMatch(Path.GetFileName(x.FullName)));

                if (fileName == null)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageOperations.UserMessage(Resources.NoIronmanSaves, MessageType.BackupError);
                    });
                    return null;
                }

                Campaign = GetCampaignFromFileName(fileName.Name);

                if (Campaign == -1)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageOperations.UserMessage(Resources.CampaignNotFound, MessageType.BackupError);
                    });
                    return null;
                }

                //Create our backup directory and file names
                var backupFileName = BuildBackupName();
                var backupChildDirectory = BuildBackupLocation();
                var backupFullName = Path.Combine(backupChildDirectory, backupFileName);

                File.Copy(fileName.FullName, backupFullName, false);

                //Only delete additional backups if the new backup was copied successfully
                if (MaxBackups > 0)
                {
                    DeleteAdditionalBackups(backupChildDirectory);
                }

                return DateTime.Now;
            }
            catch (IOException)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageOperations.UserMessage("文件被占用，无法备份", MessageType.FileInUseError);
                });
                return null;
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageOperations.UserMessage($"备份时发生错误: {ex.Message}", MessageType.BackupError);
                });
                return null;
            }
        }
        private void ResetBackup()
        {
            // 不要在这里设置 BackupActive，因为已经在 StopBackup 中设置了
            CancelBackupSource.Cancel();

            // 确保UI更新在主线程
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                MainWindow._MainWindow.SaveTextbox.IsEnabled = true;
                MainWindow._MainWindow.BackupTextbox.IsEnabled = true;
                MainWindow._MainWindow.StartButton.IsEnabled = true;
                MainWindow._MainWindow.StopButton.IsEnabled = false;
            });
        }

    }
}
