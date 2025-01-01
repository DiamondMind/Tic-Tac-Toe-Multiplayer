using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;

namespace DiamondMind.Prototypes.TicTacToe
{
    public class GameLogic : NetworkBehaviour
    {
        [SerializeField] private GameManager _gameManager;
        [SerializeField] private Sprite xSprite;
        [SerializeField] private Sprite oSprite;

        // 2D array to hold the button components
        private Button[,] buttons = new Button[3, 3];

        private void   Awake()
        {
            if(_gameManager == null)
                _gameManager = FindAnyObjectByType<GameManager>();  
        }

        public override void OnNetworkSpawn()
        {
            var cells = GetComponentsInChildren<Button>();
            int n = 0;

            // Assign buttons to the 2D array and set up click listeners
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    buttons[row, col] = cells[n];
                    n++;

                    int currentRow = row;
                    int currentCol = col;
                    // Add listener to handle button click
                    buttons[row, col].onClick.AddListener(() => OnClickCell(currentRow, currentCol));
                }
            }

        }

        private void OnClickCell(int row, int col)
        {
            if (_gameManager.gameFinished.Value == true)
                return;

            // If host's turn, set sprite to X and notify clients
            if (IsHost && _gameManager.currentTurn.Value == 0)
            {
                buttons[row, col].GetComponent<Image>().sprite = xSprite;
                buttons[row, col].interactable = false;
                ChangeSpriteClientRpc(row, col); // Notify clients to change sprite
                CheckResult(row, col);

                UpdateTurnOnHost();
            }
            // If client's turn, set sprite to O and notify host
            else if (!IsHost && _gameManager.currentTurn.Value == 1)
            {
                buttons[row, col].GetComponent<Image>().sprite = oSprite;
                buttons[row, col].interactable = false;
                ChangeSpriteServerRpc(row, col); // Notify server to change sprite
                CheckResult(row, col);
                RequestTurnUpdateServerRpc();
            }

        }

        // Client RPC to change sprite on client side
        [ClientRpc]
        private void ChangeSpriteClientRpc(int r, int c)
        {
            buttons[r, c].GetComponent<Image>().sprite = xSprite;
            buttons[r, c].interactable = false;
        }

        // Server RPC to change sprite on server side
        [ServerRpc(RequireOwnership = false)]
        private void ChangeSpriteServerRpc(int r, int c)
        {
            buttons[r, c].GetComponent<Image>().sprite = oSprite;
            buttons[r, c].interactable = false;
        }

        private void UpdateTurnOnHost()
        {
            if (NetworkManager.Singleton.IsHost)
            {
                if (_gameManager.currentTurn.Value == 0)
                    _gameManager.currentTurn.Value = 1;
                else
                    _gameManager.currentTurn.Value = 0;

                _gameManager.UpdateTurnIndicator(); 
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestTurnUpdateServerRpc()
        {
            UpdateTurnOnHost();
        }

        private void CheckResult(int r, int c)
        {
            // Get the sprite of the winning button to identify the player
            Sprite winningSprite = buttons[r, c].GetComponent<Image>().sprite;

            if (IsWon(r, c))
            {
                // Determine which player won based on the sprite
                if (winningSprite == xSprite)
                {
                    _gameManager.FinishGame("player1_won");
                }
                else if (winningSprite == oSprite)
                {
                    _gameManager.FinishGame("player2_won");
                }
            }
            else if (IsGameDraw())
            {
                _gameManager.FinishGame("draw");
            }
        }

        public bool IsWon(int r, int c)
        {
            Sprite clickedButtonSprite = buttons[r, c].GetComponent<Image>().sprite;

            // Check column
            if (buttons[0, c].GetComponent<Image>().sprite == clickedButtonSprite &&
                buttons[1, c].GetComponent<Image>().sprite == clickedButtonSprite &&
                buttons[2, c].GetComponent<Image>().sprite == clickedButtonSprite)
            {
                return true;
            }

            // Check row
            if (buttons[r, 0].GetComponent<Image>().sprite == clickedButtonSprite &&
                buttons[r, 1].GetComponent<Image>().sprite == clickedButtonSprite &&
                buttons[r, 2].GetComponent<Image>().sprite == clickedButtonSprite)
            {
                return true;
            }

            // Check first diagonal
            if (buttons[0, 0].GetComponent<Image>().sprite == clickedButtonSprite &&
                buttons[1, 1].GetComponent<Image>().sprite == clickedButtonSprite &&
                buttons[2, 2].GetComponent<Image>().sprite == clickedButtonSprite)
            {
                return true;
            }

            // Check second diagonal
            if (buttons[0, 2].GetComponent<Image>().sprite == clickedButtonSprite &&
                buttons[1, 1].GetComponent<Image>().sprite == clickedButtonSprite &&
                buttons[2, 0].GetComponent<Image>().sprite == clickedButtonSprite)
            {
                return true;
            }

            return false;
        }

        private bool IsGameDraw()
        {
            foreach (Button button in buttons)
            {
                Sprite buttonSprite = button.GetComponent<Image>().sprite;

                // If any button does not have an X or O sprite, the game is not a draw
                if (buttonSprite != xSprite && buttonSprite != oSprite)
                {
                    return false;
                }
            }

            // If all buttons are occupied by X or O, the game is a draw
            return true;
        }

    }

}
