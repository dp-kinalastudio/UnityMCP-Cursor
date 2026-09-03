using UnityEngine;
using UnityEditor;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Microsoft.CSharp;
using System.CodeDom.Compiler;

namespace UnityMCP.Editor
{
    [InitializeOnLoad]
    public class UnityMCPConnection
    {
        private static ClientWebSocket webSocket;
        private static bool isConnected = false;
        private static readonly string projectPreferencePrefix = $"UnityMCP.{PlayerSettings.productGUID}.";
        private static readonly string autoConnectEditorPref = projectPreferencePrefix + "AutoConnectEnabled";
        private static readonly string serverPortEditorPref = projectPreferencePrefix + "ServerPort";
        private static int serverPort = EditorPrefs.GetInt(serverPortEditorPref, 8080);
        private static Uri serverUri = BuildServerUri(serverPort);
        private static bool autoConnectEnabled = EditorPrefs.GetBool(autoConnectEditorPref, true);
        private static string lastErrorMessage = "";
        private static bool connectionAttemptInProgress;
        private static int consecutiveConnectionFailures;
        private static bool outageReported;
        private static bool intentionalDisconnect;
        private static double nextReconnectAt;
        private const double initialReconnectDelaySeconds = 5.0;
        private const double maxReconnectDelaySeconds = 300.0;
        private static readonly Queue<LogEntry> logBuffer = new Queue<LogEntry>();
        private static readonly int maxLogBufferSize = 1000;
        private static bool isLoggingEnabled = true;
        private const int editorStateIntervalMs = 5000;
        private const double sceneStateRefreshInterval = 30.0;
        private const double projectStructureRefreshInterval = 120.0;
        private static double nextSceneStateRefresh;
        private static double nextProjectStructureRefresh;
        private static List<string> cachedActiveGameObjects = new List<string>();
        private static object cachedSceneHierarchy = new List<object>();
        private static object cachedProjectStructure = new
        {
            scenes = new string[0],
            prefabs = new string[0],
            scripts = new string[0]
        };
    
        // Public properties for the debug window
        public static bool IsConnected => isConnected;
        public static Uri ServerUri => serverUri;
        public static int ServerPort
        {
            get => serverPort;
            set
            {
                int validPort = Mathf.Clamp(value, 1, 65535);
                if (serverPort == validPort)
                {
                    return;
                }

                serverPort = validPort;
                serverUri = BuildServerUri(serverPort);
                EditorPrefs.SetInt(serverPortEditorPref, serverPort);

                Disconnect();
                nextReconnectAt = EditorApplication.timeSinceStartup;
            }
        }
        public static string LastErrorMessage => lastErrorMessage;
        public static bool AutoConnectEnabled
        {
            get => autoConnectEnabled;
            set
            {
                if (autoConnectEnabled == value)
                {
                    return;
                }

                autoConnectEnabled = value;
                EditorPrefs.SetBool(autoConnectEditorPref, value);

                if (value)
                {
                    consecutiveConnectionFailures = 0;
                    outageReported = false;
                    nextReconnectAt = 0;
                    ConnectToServer();
                }
                else
                {
                    Disconnect("[UnityMCP] Automatic connection disabled.");
                }
            }
        }
        public static bool IsLoggingEnabled
        {
            get => isLoggingEnabled;
            set
            {
                isLoggingEnabled = value;
                if (value)
                {
                    Application.logMessageReceived += HandleLogMessage;
                }
                else
                {
                    Application.logMessageReceived -= HandleLogMessage;
                }
            }
        }
    
        private class LogEntry
        {
            public string Message { get; set; }
            public string StackTrace { get; set; }
            public LogType Type { get; set; }
            public DateTime Timestamp { get; set; }
        }

        // Public method to manually retry connection
        public static void RetryConnection()
        {
            Debug.Log("[UnityMCP] Manually retrying connection...");
            consecutiveConnectionFailures = 0;
            outageReported = false;
            nextReconnectAt = 0;
            ConnectToServer();
        }
        private static readonly CancellationTokenSource cts = new CancellationTokenSource();

        // Constructor called on editor startup
        static UnityMCPConnection()
        {
            // Start capturing logs before anything else
            Application.logMessageReceived += HandleLogMessage;
            isLoggingEnabled = true;

            Debug.Log("[UnityMCP] Plugin initialized");
            EditorApplication.delayCall += () =>
            {
                //Debug.Log("[UnityMCP] Starting initial connection");
                if (autoConnectEnabled)
                {
                    ConnectToServer();
                }
            };
            EditorApplication.update += Update;
        }

        private static void HandleLogMessage(string message, string stackTrace, LogType type)
        {
            if (!isLoggingEnabled) return;

            var logEntry = new LogEntry
            {
                Message = message,
                StackTrace = stackTrace,
                Type = type,
                Timestamp = DateTime.UtcNow
            };

            lock (logBuffer)
            {
                logBuffer.Enqueue(logEntry);
                while (logBuffer.Count > maxLogBufferSize)
                {
                    logBuffer.Dequeue();
                }
            }

            // Send log to server if connected
            if (isConnected && webSocket?.State == WebSocketState.Open)
            {
                SendLogToServer(logEntry);
            }
        }

        private static async void SendLogToServer(LogEntry logEntry)
        {
            try
            {
                var message = JsonConvert.SerializeObject(new
                {
                    type = "log",
                    data = new
                    {
                        message = logEntry.Message,
                        stackTrace = logEntry.StackTrace,
                        logType = logEntry.Type.ToString(),
                        timestamp = logEntry.Timestamp
                    }
                });

                var buffer = Encoding.UTF8.GetBytes(message);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cts.Token);
            }
            catch (Exception e)
            {
                Debug.LogError($"[UnityMCP] Failed to send log to server: {e.Message}");
            }
        }

        public static string[] GetRecentLogs(LogType[] types = null, int count = 100)
        {
            lock (logBuffer)
            {
                var logs = logBuffer.ToArray()
                    .Where(log => types == null || types.Contains(log.Type))
                    .TakeLast(count)
                    .Select(log => $"[{log.Timestamp:yyyy-MM-dd HH:mm:ss}] [{log.Type}] {log.Message}")
                    .ToArray();
                return logs;
            }
        }

        private static async void ConnectToServer()
        {
            if (!autoConnectEnabled)
            {
                return;
            }

            if (connectionAttemptInProgress)
            {
                return;
            }

            if (webSocket != null &&
                (webSocket.State == WebSocketState.Connecting ||
                 webSocket.State == WebSocketState.Open))
            {
               // Debug.Log("[UnityMCP] Already connected or connecting");
                return;
            }

            connectionAttemptInProgress = true;

            try
            {
                webSocket?.Dispose();
                webSocket = new ClientWebSocket();
                webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                
                var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
                
                await webSocket.ConnectAsync(serverUri, linkedCts.Token);

                if (!autoConnectEnabled)
                {
                    return;
                }

                isConnected = true;
                lastErrorMessage = "";
                consecutiveConnectionFailures = 0;
                nextReconnectAt = 0;

                if (outageReported)
                {
                    Debug.Log("[UnityMCP] MCP Server connection restored.");
                }

                outageReported = false;
                StartReceiving(webSocket);
                StartSendingEditorState(webSocket);
            }
            catch (OperationCanceledException)
            {
                RecordConnectionFailure("Connection attempt timed out.");
            }
            catch (WebSocketException we)
            {
                string detail = we.InnerException?.Message;
                RecordConnectionFailure(string.IsNullOrEmpty(detail)
                    ? we.Message
                    : $"{we.Message} ({detail})");
            }
            catch (Exception e)
            {
                RecordConnectionFailure($"{e.GetType().Name}: {e.Message}");
            }
            finally
            {
                connectionAttemptInProgress = false;

                if (!isConnected)
                {
                    webSocket?.Dispose();
                    webSocket = null;
                }
            }
        }

        private static Uri BuildServerUri(int port) => new Uri($"ws://localhost:{port}");

        private static void RecordConnectionFailure(string reason)
        {
            isConnected = false;

            if (!autoConnectEnabled || intentionalDisconnect)
            {
                lastErrorMessage = "";
                return;
            }

            lastErrorMessage = $"[UnityMCP] Cannot connect to MCP Server at {serverUri}: {reason}";
            consecutiveConnectionFailures++;

            double delay = Math.Min(
                initialReconnectDelaySeconds * Math.Pow(2, consecutiveConnectionFailures - 1),
                maxReconnectDelaySeconds);
            nextReconnectAt = EditorApplication.timeSinceStartup + delay;

            // One warning explains the outage. Subsequent retries are intentionally silent; the
            // successful reconnect is logged when the server comes back.
            if (!outageReported)
            {
                Debug.LogWarning($"{lastErrorMessage} Retrying silently in the background.");
                outageReported = true;
            }
        }

        private static void Update()
        {
            if (autoConnectEnabled && !isConnected && !connectionAttemptInProgress
                && EditorApplication.timeSinceStartup >= nextReconnectAt)
            {
                ConnectToServer();
            }
        }

        private static void Disconnect(string message = null)
        {
            intentionalDisconnect = true;
            isConnected = false;
            consecutiveConnectionFailures = 0;
            outageReported = false;
            nextReconnectAt = double.PositiveInfinity;
            lastErrorMessage = "";

            if (webSocket != null)
            {
                try
                {
                    webSocket.Abort();
                    webSocket.Dispose();
                }
                catch (Exception)
                {
                    // The socket may already be tearing down on its receive task.
                }
                finally
                {
                    webSocket = null;
                }
            }

            intentionalDisconnect = false;

            if (!string.IsNullOrEmpty(message))
            {
                Debug.Log(message);
            }
        }

        private static async void StartReceiving(ClientWebSocket socket)
        {
            var buffer = new byte[1024 * 4];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        HandleMessage(message);
                    }
                }
            }
            catch (Exception e)
            {
                if (autoConnectEnabled && ReferenceEquals(webSocket, socket))
                {
                    Debug.LogError($"Error receiving message: {e.Message}");
                }
                if (ReferenceEquals(webSocket, socket))
                {
                    isConnected = false;
                }
            }
        }

        private static void HandleMessage(string message)
        {
            try
            {
                var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(message);
                switch (data["type"].ToString())
                {
                    case "selectGameObject":
                        SelectGameObject(data["data"].ToString());
                        break;
                    case "togglePlayMode":
                        TogglePlayMode();
                        break;
                    case "executeEditorCommand":
                        ExecuteEditorCommand(data["data"].ToString());
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error handling message: {e.Message}");
            }
        }

        private static async void ExecuteEditorCommand(string commandData)
        {
            var logs = new List<string>();
            var errors = new List<string>();
            var warnings = new List<string>();

            Application.logMessageReceived += LogHandler;

            try
            {
                var commandObj = JsonConvert.DeserializeObject<EditorCommandData>(commandData);
                var code = commandObj.code;
                
                Debug.Log($"[UnityMCP] Executing command:\n{code}");
// Execute the code directly in the Editor context
try
{
    // Execute the provided code
    var result = CSEditorHelper.ExecuteCommand(code);

    // Send back detailed execution results
                    // Send back detailed execution results
                    var resultMessage = JsonConvert.SerializeObject(new
                    {
                        type = "commandResult",
                        data = new
                        {
                            result = result,
                            logs = logs,
                            errors = errors,
                            warnings = warnings,
                            executionSuccess = true
                        }
                    });
                    var buffer = Encoding.UTF8.GetBytes(resultMessage);
                    await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cts.Token);
                }
                catch (Exception e)
                {
                    throw new Exception($"Failed to execute command: {e.Message}", e);
                }
            }
            catch (Exception e)
            {
                var error = $"[UnityMCP] Failed to execute editor command: {e.Message}\n{e.StackTrace}";
                Debug.LogError(error);
                
                // Send back error information
                var errorMessage = JsonConvert.SerializeObject(new
                {
                    type = "commandResult",
                    data = new
                    {
                        result = (object)null,
                        logs = logs,
                        errors = new List<string>(errors) { error },
                        warnings = warnings,
                        executionSuccess = false,
                        errorDetails = new
                        {
                            message = e.Message,
                            stackTrace = e.StackTrace,
                            type = e.GetType().Name
                        }
                    }
                });
                var buffer = Encoding.UTF8.GetBytes(errorMessage);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cts.Token);
            }
            finally
            {
                Application.logMessageReceived -= LogHandler;
            }

            void LogHandler(string message, string stackTrace, LogType type)
            {
                switch (type)
                {
                    case LogType.Log:
                        logs.Add(message);
                        break;
                    case LogType.Warning:
                        warnings.Add(message);
                        break;
                    case LogType.Error:
                    case LogType.Exception:
                        errors.Add($"{message}\n{stackTrace}");
                        break;
                }
            }
        }

        private class EditorCommandData
        {
            public string code { get; set; }
        }

        private static void SelectGameObject(string objectPath)
        {
            var obj = GameObject.Find(objectPath);
            if (obj != null)
            {
                Selection.activeGameObject = obj;
            }
            else
            {
                Debug.LogWarning($"GameObject not found: {objectPath}");
            }
        }

        private static void TogglePlayMode()
        {
            EditorApplication.isPlaying = !EditorApplication.isPlaying;
        }

        private static async void StartSendingEditorState(ClientWebSocket socket)
        {
            while (isConnected && socket.State == WebSocketState.Open)
            {
                try
                {
                    var state = GetEditorState();
                    var message = JsonConvert.SerializeObject(new
                    {
                        type = "editorState",
                        data = state
                    });
                    var buffer = Encoding.UTF8.GetBytes(message);
                    await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cts.Token);
                    await Task.Delay(editorStateIntervalMs);
                }
                catch (Exception e)
                {
                    if (autoConnectEnabled && ReferenceEquals(webSocket, socket))
                    {
                        Debug.LogError($"Error sending editor state: {e.Message}");
                    }
                    if (ReferenceEquals(webSocket, socket))
                    {
                        isConnected = false;
                    }
                    break;
                }
            }
        }

        private static object GetEditorState()
        {
            try
            {
                var selectedObjects = new List<string>();

                var selection = Selection.gameObjects;
                if (selection != null)
                {
                    foreach (var obj in selection)
                    {
                        if (obj != null && !string.IsNullOrEmpty(obj.name))
                        {
                            selectedObjects.Add(obj.name);
                        }
                    }
                }

                RefreshSceneStateCacheIfNeeded();
                RefreshProjectStructureCacheIfNeeded();

                return new
                {
                    activeGameObjects = cachedActiveGameObjects,
                    selectedObjects,
                    playModeState = EditorApplication.isPlaying ? "Playing" : "Stopped",
                    sceneHierarchy = cachedSceneHierarchy,
                    projectStructure = cachedProjectStructure
                };
            }
            catch (Exception e)
            {
                lastErrorMessage = $"Error getting editor state: {e.Message}";
                Debug.LogError(lastErrorMessage);
                return new
                {
                    activeGameObjects = new List<string>(),
                    selectedObjects = new List<string>(),
                    playModeState = "Unknown",
                    sceneHierarchy = new List<object>(),
                    projectStructure = new { scenes = new string[0], prefabs = new string[0], scripts = new string[0] }
                };
            }
        }

        private static void RefreshSceneStateCacheIfNeeded()
        {
            if (EditorApplication.timeSinceStartup < nextSceneStateRefresh) return;

            nextSceneStateRefresh = EditorApplication.timeSinceStartup + sceneStateRefreshInterval;
            var activeGameObjects = new List<string>();

            var foundObjects = GameObject.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            if (foundObjects != null)
            {
                foreach (var obj in foundObjects)
                {
                    if (obj != null && !string.IsNullOrEmpty(obj.name))
                    {
                        activeGameObjects.Add(obj.name);
                    }
                }
            }

            var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            cachedActiveGameObjects = activeGameObjects;
            cachedSceneHierarchy = currentScene.IsValid() ? GetSceneHierarchy() : new List<object>();
        }

        private static void RefreshProjectStructureCacheIfNeeded()
        {
            if (EditorApplication.timeSinceStartup < nextProjectStructureRefresh) return;

            nextProjectStructureRefresh = EditorApplication.timeSinceStartup + projectStructureRefreshInterval;
            cachedProjectStructure = new
            {
                scenes = GetSceneNames() ?? new string[0],
                prefabs = GetPrefabPaths() ?? new string[0],
                scripts = GetScriptPaths() ?? new string[0]
            };
        }

        private static object GetSceneHierarchy()
        {
            try
            {
                var roots = new List<object>();
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                
                if (scene.IsValid())
                {
                    var rootObjects = scene.GetRootGameObjects();
                    if (rootObjects != null)
                    {
                        foreach (var root in rootObjects)
                        {
                            if (root != null)
                            {
                                try
                                {
                                    roots.Add(GetGameObjectHierarchy(root));
                                }
                                catch (Exception e)
                                {
                                    Debug.LogWarning($"[UnityMCP] Failed to get hierarchy for {root.name}: {e.Message}");
                                }
                            }
                        }
                    }
                }
                
                return roots;
            }
            catch (Exception e)
            {
                lastErrorMessage = $"Error getting scene hierarchy: {e.Message}";
                Debug.LogError(lastErrorMessage);
                return new List<object>();
            }
        }

        private static object GetGameObjectHierarchy(GameObject obj)
        {
            try
            {
                if (obj == null) return null;

                var children = new List<object>();
                var transform = obj.transform;
                
                if (transform != null)
                {
                    for (int i = 0; i < transform.childCount; i++)
                    {
                        try
                        {
                            var childTransform = transform.GetChild(i);
                            if (childTransform != null && childTransform.gameObject != null)
                            {
                                var childHierarchy = GetGameObjectHierarchy(childTransform.gameObject);
                                if (childHierarchy != null)
                                {
                                    children.Add(childHierarchy);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[UnityMCP] Failed to process child {i} of {obj.name}: {e.Message}");
                        }
                    }
                }

                return new
                {
                    name = obj.name ?? "Unnamed",
                    components = GetComponentNames(obj),
                    children = children
                };
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UnityMCP] Failed to get hierarchy for {(obj != null ? obj.name : "null")}: {e.Message}");
                return null;
            }
        }

        private static string[] GetComponentNames(GameObject obj)
        {
            try
            {
                if (obj == null) return new string[0];

                var components = obj.GetComponents<Component>();
                if (components == null) return new string[0];

                var validComponents = new List<string>();
                foreach (var component in components)
                {
                    try
                    {
                        if (component != null)
                        {
                            validComponents.Add(component.GetType().Name);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[UnityMCP] Failed to get component name: {e.Message}");
                    }
                }

                return validComponents.ToArray();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UnityMCP] Failed to get component names for {(obj != null ? obj.name : "null")}: {e.Message}");
                return new string[0];
            }
        }

        private static object GetProjectStructure()
        {
            // Simplified project assets structure
            return new
            {
                scenes = GetSceneNames(),
                prefabs = GetPrefabPaths(),
                scripts = GetScriptPaths()
            };
        }

        private static string[] GetSceneNames()
        {
            var scenes = new List<string>();
            foreach (var scene in EditorBuildSettings.scenes)
            {
                scenes.Add(scene.path);
            }
            return scenes.ToArray();
        }

        private static string[] GetPrefabPaths()
        {
            var guids = AssetDatabase.FindAssets("t:Prefab");
            var paths = new string[guids.Length];
            for (int i = 0; i < guids.Length; i++)
            {
                paths[i] = AssetDatabase.GUIDToAssetPath(guids[i]);
            }
            return paths;
        }

        private static string[] GetScriptPaths()
        {
            var guids = AssetDatabase.FindAssets("t:Script");
            var paths = new string[guids.Length];
            for (int i = 0; i < guids.Length; i++)
            {
                paths[i] = AssetDatabase.GUIDToAssetPath(guids[i]);
            }
            return paths;
        }
    
        public static class CSEditorHelper
        {
            public static object ExecuteCommand(string code)
            {
                // Create a method that wraps the code
                string wrappedCode = $@"
                    using UnityEngine;
                    using UnityEditor;
                    using System;
                    using System.Linq;
                    using System.Collections.Generic;
    
                    public class CodeExecutor
                    {{
                        public static object Execute()
                        {{
                            {code}
                            return ""Success"";
                        }}
                    }}
                ";
    
                // Use Mono's built-in compiler
                var options = new System.CodeDom.Compiler.CompilerParameters
                {
                    GenerateInMemory = true
                };
                
                // Add necessary references
                options.ReferencedAssemblies.Add(typeof(UnityEngine.Object).Assembly.Location);
                options.ReferencedAssemblies.Add(typeof(UnityEditor.Editor).Assembly.Location);
                options.ReferencedAssemblies.Add(typeof(System.Linq.Enumerable).Assembly.Location); // Add System.Core for LINQ
                options.ReferencedAssemblies.Add(typeof(object).Assembly.Location); // Add mscorlib
                options.ReferencedAssemblies.Add(AppDomain.CurrentDomain.GetAssemblies()
                    .First(a => a.GetName().Name == "netstandard").Location); // Add netstandard
                
                // Compile and execute
                using (var provider = new Microsoft.CSharp.CSharpCodeProvider())
                {
                    var results = provider.CompileAssemblyFromSource(options, wrappedCode);
                    if (results.Errors.HasErrors)
                    {
                        var errors = string.Join("\n", results.Errors.Cast<CompilerError>().Select(e => e.ErrorText));
                        throw new Exception($"Compilation failed:\n{errors}");
                    }
    
                    var assembly = results.CompiledAssembly;
                    var type = assembly.GetType("CodeExecutor");
                    var method = type.GetMethod("Execute");
                    return method.Invoke(null, null);
                }
            }
        }
    }
}
