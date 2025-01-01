using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using UnityEngine.UI;
using System;
using System.Threading.Tasks;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace DiamondMind.Prototypes.TicTacToe
{
    public class GameManager : NetworkBehaviour
    {

        #region ---------- Variables ----------

        [Header("---------- Network Variables ----------")]
        public NetworkVariable<int> currentTurn = new NetworkVariable<int>(0);
        public NetworkVariable<bool> gameFinished = new NetworkVariable<bool>(false);
        [SerializeField] private NetworkVariable<bool> gameStarted = new NetworkVariable<bool>(false);
        [SerializeField] private NetworkVariable<int> playersReady = new NetworkVariable<int>(0);
        [SerializeField] private NetworkVariable<float> countdownTime = new NetworkVariable<float>(0f);
        [SerializeField] private NetworkVariable<bool> clientDisconnected = new NetworkVariable<bool>(false);
        [SerializeField] private NetworkVariable<float> gracePeriodRemaining = new NetworkVariable<float>(10f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        [SerializeField] private NetworkVariable<int> clientReconnectionAttempts = new NetworkVariable<int>(0);

        public bool debugMode;
        [Header("---------- Login ----------")]
        [SerializeField] private GameObject loginPanel;
        [SerializeField] private Text joinCodeText;
        [SerializeField] private InputField joinCodeInput;
        [SerializeField] private Button hostButton;
        [SerializeField] private Button joinButton;
        [SerializeField] private Text messageTxt;
        [SerializeField] private float readyScreenTimeout = 30f;

        [Header("---------- Gameplay ----------")]
        [SerializeField] private GameObject boardPrefab;
        [SerializeField] private GameObject player1Indicator;
        [SerializeField] private GameObject player2Indicator;
        [SerializeField] private GameObject hostTurnIndicator;
        [SerializeField] private GameObject clientTurnIndicator;

        [Header("---------- Game End ----------")]
        [SerializeField] private GameObject gameEndPanel;
        [SerializeField] private Image player1WinsImg;
        [SerializeField] private Image player2WinsImg;
        [SerializeField] private Image noWinnerImg;
        [SerializeField] private GameObject quitButton;

        [Header("---------- Game Management ----------")]
        [SerializeField] private float reconnectTime = 30f;
        [SerializeField] private int maxReconnectionAttempts = 3;
        [SerializeField] private Text hostWaitingTxt;
        [SerializeField] private Text clientMsgTxt;
        [SerializeField] private Text clientCountdownTxt;
        [SerializeField] private Button reconnectButton;
        [SerializeField] private GameObject readyPanel;

        [Header("---------- Events ----------")]
        [SerializeField] private UnityEvent onClientsConnected;
        [SerializeField] private UnityEvent onReadyScreenTimeout;
        [SerializeField] private UnityEvent onStartGame;
        [SerializeField] private UnityEvent onGameFinished;
        [SerializeField] private UnityEvent onPlayerDisconnect;
        [SerializeField] private UnityEvent onHostDisconnect;
        [SerializeField] private UnityEvent onClientDisconnect_host;
        [SerializeField] private UnityEvent onClientDisconnect_client;
        [SerializeField] private UnityEvent onClientPassMaxReconnectionAttempts;
        [SerializeField] private UnityEvent onClientFailToReconnect_host;
        [SerializeField] private UnityEvent onClientFailToReconnect_client;
        [SerializeField] private UnityEvent onClientReconnect;
        [SerializeField] private UnityEvent onResumeGame;
        [SerializeField] private UnityEvent onTransportFailure;

        ulong disconnectedClientId = 999;
        string sessionId;
        bool isCountdownActive_client;
        GameObject newBoard;

        Coroutine startTimeoutCoroutine;
        Coroutine disconnectionCoroutine;
        Coroutine clientDisconnectRoutine;
        Coroutine host_gracePeriodCoroutine;
        Coroutine client_gracePeriodCoroutine;

# endregion


        #region ---------- Unity Methods ----------

        private async void Start()
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
                NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
                NetworkManager.Singleton.OnTransportFailure += HandleTransportFailure;
            }
            else
            {
                if(debugMode) Debug.LogError("Mo network manager in the scene");
            }

            try
            {
                // Initialize Unity Services
                await UnityServices.InitializeAsync();
                // Check if the player is already signed in
                if (!AuthenticationService.Instance.IsSignedIn)
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }
            catch (Exception e)
            {
                if (debugMode) Debug.LogError("Initialization error: " + e.Message);
            }
        }

        public override void OnDestroy()
        {
            if (NetworkManager.Singleton == null)
                return;

            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
            NetworkManager.Singleton.OnTransportFailure -= HandleTransportFailure;

        }

        private void Update()
        {
#if UNITY_EDITOR

            // TESTING HOST AND CLIENT DISCONNECTIONS
            if (IsHost && Input.GetKeyDown(KeyCode.H) && gameStarted.Value)
            {
                if(debugMode) Debug.Log("Simulating host disconnection...");
                NetworkManager.Singleton.Shutdown();
            }

            if (!IsHost && Input.GetKeyDown(KeyCode.C) && gameStarted.Value)
            {
                if(debugMode) Debug.Log($"Simulating client disconnection for client ID: {NetworkManager.Singleton.LocalClientId}");
                RequestRemoveClientServerRpc(NetworkManager.LocalClientId);
            }

#endif
        }


        #endregion


        #region ---------- Login ----------

        public async void HostGame()
        {
            try
            {
                hostButton.interactable = false;
                joinButton.interactable = false;
                messageTxt.text = "Hosting game";

                // End any existing session before starting a new one
                if (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsClient)
                {
                    if (debugMode) Debug.Log("Ending existing session...");
                    NetworkManager.Singleton.Shutdown();

                    // Wait until the shutdown process is complete
                    while (NetworkManager.Singleton.ShutdownInProgress)
                    {
                        await Task.Yield(); // Yield control and check again on the next frame
                    }
                }

                // Allocate a Relay server slot for 1 client
                Allocation allocation = await RelayService.Instance.CreateAllocationAsync(1);
                // Get the join code for client to connect
                string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
                sessionId = joinCode;
                joinCodeText.text = joinCode;

                // Set Relay server data for the Unity Transport
                RelayServerData relayServerData = new RelayServerData(allocation, "wss");
                NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData(relayServerData);
                NetworkManager.Singleton.StartHost(); // Start hosting the game
                messageTxt.text = "Waiting for client to join the game...";

                // Start the 30-second timeout coroutine
                //StartCoroutine(TimeoutReenableButtons());
            }
            catch (RelayServiceException e)
            {
                if (e.Message.Contains("Could not do Qos region selection. Will use default", StringComparison.OrdinalIgnoreCase))
                {
                    messageTxt.text = "Failed to host game, check your internet connection and try again";
                }
                else
                {
                    if (debugMode) Debug.LogError($"Failed to host the game. Error: {e.Message}");
                    messageTxt.text = "Error: Failed to host game.";
                }

                hostButton.interactable = true;
                joinButton.interactable = true;
            }
        }

        public async void JoinGame()
        {
            if (string.IsNullOrWhiteSpace(joinCodeInput.text))
                return;

            try
            {
                messageTxt.text = "";
                joinButton.interactable = false;
                hostButton.interactable = false;
                sessionId = joinCodeInput.text;

                JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCodeInput.text);
                RelayServerData relayServerData = new RelayServerData(joinAllocation, "wss");
                NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData(relayServerData);
                NetworkManager.Singleton.StartClient();
                messageTxt.text = "Joining the game session...";

                // Start the 30 second timeout coroutine
                //StartCoroutine(TimeoutReenableButtons());
            }
            catch (RelayServiceException e)
            {
                // Check the message or error code to see if it's an invalid join code
                if (e.Message.Contains("join code not found", StringComparison.OrdinalIgnoreCase))
                {
                    messageTxt.text = "Invalid join code. Please check the code and try again.";
                }
                else
                {
                    // Handle other RelayService exceptions
                    if (debugMode) Debug.LogError($"Failed to join the game. Error: {e.Message}");
                    messageTxt.text = "Error: Failed to join game, Try again";
                }

                joinButton.interactable = true;
                hostButton.interactable = true;
            }
        }

        private IEnumerator TimeoutReenableButtons()
        {
            yield return new WaitForSeconds(30f);

            // Check if still neither a host nor client
            if (!NetworkManager.Singleton.IsHost && !NetworkManager.Singleton.IsClient)
            {
                hostButton.interactable = true;
                joinButton.interactable = true;
                messageTxt.text = "Failed to connect. Try hosting or joining again.";
            }
        }

        private void OnClientConnected(ulong clientId)
        {
            // ----- INITIAL CONNECTION -----
            if (debugMode) Debug.Log("Client with id " + clientId + " joined");

            if (IsHost && NetworkManager.Singleton.ConnectedClients.Count == 2)
            {
                OnClientsConnectedClientRPC();
            }

            // ----- RECONNECTION -----
            // Check if the game is still active
            if (IsHost && clientDisconnected.Value == true && gracePeriodRemaining.Value >= 0f && NetworkManager.Singleton.ConnectedClients.Count == 2)
            {
                clientReconnectionAttempts.Value += 1;
                // Allow reconnection
                if(debugMode) Debug.Log($"Client {clientId} has reconnected.");
                clientDisconnected.Value = false;
                disconnectedClientId = 999; // Clear the disconnected client ID
                CheckAndResumeGameClientRPC();
            }
        }

        [ClientRpc]
        private void OnClientsConnectedClientRPC()
        {
            onClientsConnected?.Invoke();
        }

        public void TryStartGame()
        {
            if (gameStarted.Value)
                return;

            SubmitReadyServerRpc();
            startTimeoutCoroutine = StartCoroutine(StartTimeout());
        }

        private IEnumerator StartTimeout()
        {
            float countdownTime = readyScreenTimeout;

            while (countdownTime > 0)
            {
                yield return new WaitForSeconds(1f);
                countdownTime -= 1f;
            }

            // Countdown complete
            if (playersReady.Value < 2 && gameStarted.Value == false)
            {
                onReadyScreenTimeout?.Invoke();
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void SubmitReadyServerRpc()
        {
            playersReady.Value += 1;

            if (playersReady.Value > 1 && !gameStarted.Value)
            {
                gameStarted.Value = true;
                StartGameClientRpc();
            }
        }

        [ClientRpc]
        private void StartGameClientRpc()
        {
            onStartGame?.Invoke();

            if(IsHost)
            {
                StartGame();
                ToggleQuitButtonClientRPC(false);
                ShowPlayerIDClientRPC();
                UpdateTurnIndicator(); // Update the turn indicator when the game starts
            }
        }

        private void StartGame()
        {
            newBoard = Instantiate(boardPrefab);
            newBoard.GetComponent<NetworkObject>().Spawn(); // Make the board a networked object
            
            onStartGame?.Invoke();
        }

        #endregion


        #region ---------- Disconnection ----------

        private void OnClientDisconnect(ulong clientId)
        {
            if (gameFinished.Value == true)
                return;

            // Stop any ongoing timeout coroutine
            if (startTimeoutCoroutine != null)
            {
                StopCoroutine(startTimeoutCoroutine);
                startTimeoutCoroutine = null;
            }

            // Stop and restart the disconnection handling coroutine
            if (disconnectionCoroutine != null)
            {
                StopCoroutine(disconnectionCoroutine);
            }

            disconnectionCoroutine = StartCoroutine(HandleDisconnection(clientId));
        }

        private IEnumerator HandleDisconnection(ulong clientId)
        {
            // Trigger the onPlayerDisconnect event
            onPlayerDisconnect?.Invoke();

            // Start the async task to check session activity
            var sessionCheckTask = CheckSessionActiveAsync(sessionId);
            yield return new WaitUntil(() => sessionCheckTask.IsCompleted);

            if (sessionCheckTask.Exception != null)
            {
                if (debugMode) Debug.LogError($"Error while checking session activity: {sessionCheckTask.Exception.Message}");
                yield break;
            }

            // Determine if it's the host or a client that disconnected
            if (clientId == NetworkManager.ServerClientId)
            {
                if (debugMode) Debug.Log("Host disconnected");
                HandleHostDisconnection();
            }
            else
            {
                if (debugMode) Debug.Log("Client disconnected");
                HandleClientDisconnection(clientId, sessionCheckTask.Result);
            }

            // Clear the coroutine reference after completion
            disconnectionCoroutine = null;
        }

        private void HandleHostDisconnection()
        {
            if(debugMode) Debug.Log("Host disconnected. Ending game session.");
            // Display a message on the client's UI that the game is over
            onHostDisconnect?.Invoke();
            
            // Disconnect the client from the NetworkManager
            NetworkManager.Singleton.Shutdown();
        }

        private IEnumerator StartGracePeriod_Host()
        {
            float remainingTime = reconnectTime;
            float startTime = Time.realtimeSinceStartup;

            while (remainingTime > 0f && clientDisconnected.Value == true)
            {
                float elapsed = Time.realtimeSinceStartup - startTime;
                remainingTime = reconnectTime - elapsed;

                gracePeriodRemaining.Value = remainingTime;

                hostWaitingTxt.text = $"Client has {Mathf.RoundToInt(remainingTime)} seconds to reconnect";

                yield return null;
            }

            // If the client has not reconnected after the given time
            if (clientDisconnected.Value == true)
            {
                gracePeriodRemaining.Value = 0;
                hostWaitingTxt.text = $"Client {disconnectedClientId} failed to reconnect in time.";
                onClientFailToReconnect_host?.Invoke();
            }
        }

        private void HandleClientDisconnection(ulong clientId, bool sessionActive)
        {
            if (debugMode) Debug.Log($"Client with ID: {clientId} disconnected from the game.");
            disconnectedClientId = clientId;

            if (IsHost)
            {
                clientDisconnected.Value = true;
                onClientDisconnect_host?.Invoke();

                if (clientReconnectionAttempts.Value < maxReconnectionAttempts)
                    host_gracePeriodCoroutine = StartCoroutine(StartGracePeriod_Host());
                else
                {
                    onClientPassMaxReconnectionAttempts?.Invoke();
                    hostWaitingTxt.text = "Client can't connect to the game anymore. Three reconnection trials has been exhausted";

                }
            }
            else
            {
                if (sessionActive)
                {
                    if (debugMode) Debug.Log("Game session is still active");
                    onClientDisconnect_client?.Invoke();

                    if (clientReconnectionAttempts.Value < maxReconnectionAttempts)
                        client_gracePeriodCoroutine = StartCoroutine(StartGracePeriod_Client());
                    else
                    {
                        onClientPassMaxReconnectionAttempts?.Invoke();
                        NotifyClient("You can't connect to the game anymore. You have exhausted your three trials");
                    }
                }
                else
                {
                    onHostDisconnect?.Invoke();
                    if (debugMode) Debug.Log("Host is disconnected");
                }
            }
        }

        private IEnumerator StartGracePeriod_Client()
        {
            NotifyClient("Connection lost.");
            float timeLeft = reconnectTime;
            isCountdownActive_client = true;

            float endTime = Time.realtimeSinceStartup + timeLeft;
            bool timerComplete = false;

            while (!timerComplete)
            {
                float timeRemaining = Mathf.Max(0, endTime - Time.realtimeSinceStartup);
                clientCountdownTxt.text = $"Reconnection available for {Mathf.Ceil(timeRemaining)} seconds...";
                // Timer complete when timeRemaining is <= 0
                if (timeRemaining <= 0)
                {
                    timerComplete = true;
                    onClientFailToReconnect_client?.Invoke();
                }

                yield return null;
            }

            // Countdown complete
            if (clientDisconnected.Value == true)
            {
                isCountdownActive_client = false;
                NotifyClient("Reconnection window has expired. Unable to reconnect.");

                onClientFailToReconnect_client?.Invoke();
            }
        }

        public void TryReconnect()
        {
            if (isCountdownActive_client && clientReconnectionAttempts.Value < maxReconnectionAttempts)
            {
                StartCoroutine(AttemptReconnection());
            }
            else
            {
                if(debugMode) Debug.Log("You can't connect to the game anymore. You have exhausted your three trials");
            }
        }

        private IEnumerator AttemptReconnection()
        {
            NetworkManager.Singleton.Shutdown();
            yield return new WaitWhile(() => NetworkManager.Singleton.ShutdownInProgress);

            while (isCountdownActive_client)
            {
                reconnectButton.interactable = false;
                // Attempt to reconnect
                var reconnectingTask = ReconnectClientAsync();
                yield return new WaitUntil(() => reconnectingTask.IsCompleted);

                if (reconnectingTask.Result)
                {
                    NotifyClient("Reconnected successfully!");
                    yield break; // Exit if reconnection is successful
                }
                else
                {
                    NotifyClient("Reconnection attempt failed.");
                    reconnectButton.interactable = true;
                }

                yield return new WaitForSecondsRealtime(1f); // Delay before retrying
            }

            NotifyClient("Failed to reconnect. Please check your internet connection.");
        }

        private async Task<bool> ReconnectClientAsync()
        {
            try
            {
                JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(sessionId);
                RelayServerData relayServerData = new RelayServerData(joinAllocation, "wss");
                NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData(relayServerData);
                NetworkManager.Singleton.StartClient();
                NotifyClient("Reconnecting to the game session...");

                await Task.Delay(5000);

                // Check if the client is successfully connected
                if (NetworkManager.Singleton.IsClient && NetworkManager.Singleton.IsConnectedClient)
                {
                    NotifyClient("Reconnected successfully!");
                    if (debugMode) Debug.Log("Reconnected successfully!");
                    
                    return true; // Reconnection was successful
                }
                else
                {
                    if (debugMode) Debug.LogWarning("Reconnection attempt failed.");
                   
                    return false; // Reconnection failed
                }
            }
            catch (RelayServiceException e)
            {
                if (e.Message.Contains("join code not found", StringComparison.OrdinalIgnoreCase))
                {
                    // Invalid join code, notify user
                    if (debugMode) Debug.Log("Invalid join code. Cannot reconnect.");
                   
                    return false; // Do not retry for invalid join code
                }
                else
                {
                    if (debugMode) Debug.Log($"Error reconnecting: {e.Message}");
                    NotifyClient($"Error reconnecting");
                    
                    return false; // Reconnection failed due to other errors
                }
            }
        }

        private async Task<bool> CheckSessionActiveAsync(string joinCode)
        {
            try
            {
                // Wait for the Relay service to fully clean up the session
                await Task.Delay(12000);

                // Attempt to retrieve the join allocation to verify session activity
                JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

                // If we reached this point, the session is active
                
                return true;
            }
            catch (RelayServiceException e)
            {
                if (e.Message.Contains("join code not found", StringComparison.OrdinalIgnoreCase))
                {
                    if (debugMode) Debug.Log("Invalid join code. The session is not active.");
                   
                    return false; // Session is not active
                }
                else
                {
                    if (debugMode) Debug.Log($"Error checking session activity: {e.Message}");
                    
                    return false; // Handle other errors appropriately
                }
            }
        }

        private void NotifyClient(string message)
        {
            clientMsgTxt.text = message;
        }

        [ClientRpc]
        private void CheckAndResumeGameClientRPC()
        {
            if (client_gracePeriodCoroutine != null)
            {
                StopCoroutine(client_gracePeriodCoroutine);
                client_gracePeriodCoroutine = null;
            }

            if (host_gracePeriodCoroutine != null)
            {
                StopCoroutine(host_gracePeriodCoroutine);
                host_gracePeriodCoroutine = null;
            }

            if (clientDisconnectRoutine != null)
            {
                StopCoroutine(clientDisconnectRoutine);
            }

            if (gameStarted.Value == true)
            {
                readyPanel.SetActive(false);
                onResumeGame?.Invoke();
            }
            else
            {
                StartCoroutine(StartTimeout());
                readyPanel.SetActive(true);
            }


            if (debugMode) Debug.Log("Game resumed.");
            onClientReconnect?.Invoke();
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestRemoveClientServerRpc(ulong id)
        {
            NetworkManager.Singleton.DisconnectClient(id);
        }

        private void HandleTransportFailure()
        {
            if (gameFinished.Value)
                return;

            if (debugMode) Debug.LogError("Transport failure detected, ending game session");

            // Disconnect the client from the NetworkManager 
            NetworkManager.Singleton.Shutdown();

            onTransportFailure?.Invoke();
        }

        #endregion


        #region ---------- Gameplay ----------

        [ClientRpc]
        private void ShowPlayerIDClientRPC()
        {
            if (IsHost)
            {
                player1Indicator.SetActive(true);
                player2Indicator.SetActive(false);
            }
            else
            {
                player1Indicator.SetActive(false);
                player2Indicator.SetActive(true);
            }
        }

        public void UpdateTurnIndicator()
        {
            UpdateTurnIndicatorClientRpc(currentTurn.Value);
        }

        [ClientRpc]
        private void UpdateTurnIndicatorClientRpc(int turn)
        {
            // Host's turn
            if (turn == 0)
            {
                if (NetworkManager.Singleton.IsHost)
                {
                    hostTurnIndicator.SetActive(true);  
                    clientTurnIndicator.SetActive(false); 
                }
                else if (!NetworkManager.Singleton.IsHost)
                {
                    hostTurnIndicator.SetActive(false);  
                    clientTurnIndicator.SetActive(false); 
                }
            }
            // Client's turn
            else if (turn == 1)
            {
                if (!NetworkManager.Singleton.IsHost)
                {
                    clientTurnIndicator.SetActive(true); 
                    hostTurnIndicator.SetActive(false);   
                }
                else if (NetworkManager.Singleton.IsHost)
                {
                    hostTurnIndicator.SetActive(false);   
                    clientTurnIndicator.SetActive(false);
                }
            }
        }

        #endregion


        #region ---------- Game End ----------

        public void FinishGame(string msg)
        {
            if (gameFinished.Value == true)
                return;

            if (IsHost)
            {
                UpdateGameStateServerRPC(msg);
            }
            else
            {
                RequestFinishGameServerRpc(msg);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void UpdateGameStateServerRPC(string msg)
        {
            gameFinished.Value = true;
            FinishGameClientRpc(msg);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestFinishGameServerRpc(string msg)
        {
            UpdateGameStateServerRPC(msg);
        }

        [ClientRpc]
        private void FinishGameClientRpc(string msg)
        {
            ShowMsg(msg);
        }

        public void ShowMsg(string msg)
        {
            onGameFinished?.Invoke();
            // Hide all result images initially
            player1WinsImg.gameObject.SetActive(false);
            player2WinsImg.gameObject.SetActive(false);
            noWinnerImg.gameObject.SetActive(false);
            
            ToggleQuitButtonClientRPC(true);
            // Display appropriate message and image based on the result
            if (msg.Equals("player1_won"))
            {
                player1WinsImg.gameObject.SetActive(true);
                gameEndPanel.SetActive(true);
                //ShowOpponentMsg("You Lose");
            }
            else if (msg.Equals("player2_won"))
            {
                player2WinsImg.gameObject.SetActive(true);
                gameEndPanel.SetActive(true);
                //ShowOpponentMsg("You Won");
            }
            else if (msg.Equals("draw"))
            {
                noWinnerImg.gameObject.SetActive(true);
                gameEndPanel.SetActive(true);
                //ShowOpponentMsg("Game Draw");
            }
        }

        [ClientRpc]
        private void ToggleQuitButtonClientRPC(bool active)
        {
            quitButton.SetActive(active);
        }

        #endregion


        #region ---------- Restart & Quit ----------

        public void Restart()
        {
            if (IsHost)
            {
                RestartGameOnServer();
            }
            else
            {
                RequestRestartFromClientServerRPC();
            }
        }

        private void RestartGameOnServer()
        {
            if (newBoard != null)
            {
                Destroy(newBoard);
            }

            gameFinished.Value = false;
            StartGame();
            gameEndPanel.SetActive(false);

            // Notify all clients to turn off their gameEndPanel
            RestartClientRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestRestartFromClientServerRPC()
        {
            RestartGameOnServer();
        }

        [ClientRpc]
        private void RestartClientRpc()
        {
            gameEndPanel.SetActive(false);
        }

        public void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        public void ReloadScene()
        {
            // Check if the NetworkManager exists
            if (NetworkManager.Singleton != null)
            {
                if (NetworkManager.Singleton.IsListening)
                {
                    NetworkManager.Singleton.Shutdown(); // Shutdown the network
                }

                // Destroy the NetworkManager GameObject
                Destroy(NetworkManager.Singleton.gameObject);
            }

            // Reload the current scene
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
        }

        #endregion

    }
}