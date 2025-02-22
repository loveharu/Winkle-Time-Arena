using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using MNF;
using TMPro;

public enum LobbyUserState
{
    None = -1,
    Admin,
    Ready,
    NotReady
}

public class LobbyScene : MonoBehaviour
{
    public static LobbyScene Instance;
    
    [Header("로그인 패널")]
    public TextMeshProUGUI loginAlertText;
    public TMP_InputField inputUserID;
    public GameObject loginPanel;
    public Button loginButton;
    public Image ReconnectImage;
    public Button menuButton;
    public Button reconnectButton;
    [Header("로비 패널")]
    public TextMeshProUGUI lobbyButtonText;
    public Button lobbyButton;

    [Header("채팅")]
    public TextMeshProUGUI chatPrefab;
    public TMP_InputField inputChat;
    public Transform chatViewParent;
    public RectTransform chatRoot;
    public Transform chatBox;
    
    [Header("그외")]
    public GameObject playerPrefab;
    public Transform[] positions;

    private readonly Regex _regex = new ("^[a-zA-Z0-9가-힣ㄱ-ㅎㅏ-ㅣ]*$");
    private readonly List<GameObject> _characters = new();
    private const int MinUserToStart = 1;
    private const int MaxUserAmount = 5;
    private string _userId;
    
    private void Start()
    {
        lobbyButton.onClick.AddListener(OnClick_LobbyButton);
        loginButton.onClick.AddListener(OnClick_Login);
        inputChat.onSubmit.AddListener(SendChatting);
        menuButton.onClick.AddListener(OnClick_ReconnectButton);
        reconnectButton.onClick.AddListener(OnClick_QuitGame);
        
        ReconnectImage.rectTransform.gameObject.SetActive(false);
        //나중에 각자 집에서 단체로 AWS서버로 테스트해야해서 이건 지우지말것
        NetGameManager.instance.ConnectServer("3.34.116.91", 3650); 
    }

    public void OnDestroy()
    {
        Instance = null;
    }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else if (Instance != null)
        {
            // 이미 다른 인스턴스가 존재하면 현재 인스턴스를 파괴하여 초기화
            Destroy(Instance);
        }
        
    }

    private void Update()
    {
        if (!Input.GetMouseButtonDown(0)) return;
        Vector2 localMousePosition = chatRoot.InverseTransformPoint(Input.mousePosition);
        if (!chatRoot.rect.Contains(localMousePosition)) chatBox.gameObject.SetActive(false);

    }

    private void OnClick_LobbyButton()
	{
        //준비, 시작버튼 클릭시
        RoomSession roomSession = NetGameManager.instance.m_roomSession;
        
        UserSession userSession =
            NetGameManager.instance.GetRoomUserSession(NetGameManager.instance.m_userHandle.m_szUserID);
        
        if (userSession.m_nUserData[0] == (int)LobbyUserState.Admin) //어드민일 경우
        {
            if (roomSession.m_userList.Any(t => t.m_nUserData[0] == (int)LobbyUserState.NotReady))
            {
                string chatText = "<#4FB7FF><b>알림 : 준비 되지 않은 사람이 있습니다.</b></color>";
                AddChatting(chatText);
                return;
                
            }
            if (roomSession.m_userList.Count < MinUserToStart)
            {
                string chatText = "<#4FB7FF><b>알림 : 시작에 필요한 최소 인원이 부족합니다.</b></color>";
                AddChatting(chatText);
                return;
            }

            GameStart();
        }
        else
        {
            if (userSession.m_nUserData[0] == (int)LobbyUserState.Ready)
            {
                userSession.m_nUserData[0] = (int)LobbyUserState.NotReady;
            }
            else 
            {
                userSession.m_nUserData[0] = (int)LobbyUserState.Ready;
            }

            NetGameManager.instance.RoomUserDataUpdate(userSession);
        }
	}
    public void RoomEnter()
	{
        // 새로 들어왔을때
        if (!CanEnterRoom(NetGameManager.instance.m_userHandle.m_szUserID))
        {
            loginPanel.SetActive(true);
            loginAlertText.text = "로그인을 시도해 주세요";
            
            return;
        }
        
        UserSession userSession = NetGameManager.instance.GetRoomUserSession(NetGameManager.instance.m_userHandle.m_szUserID);
        string chatText = $"<#4FB7FF><b>알림 : {userSession.m_szUserID} 님이 입장하셨습니다.</b></color>";
        BroadcastChat(chatText);

        int userCount = NetGameManager.instance.m_roomSession.m_userList.Count;
        
        if (userCount == 1) //해당 로비에 유저가 본인 뿐이면 방의 방장으로 설정
        {
            lobbyButtonText.text = "게임 시작";
            userSession.m_nUserData[0] = (int)LobbyUserState.Admin;
        }
        else
        {
            lobbyButtonText.text = "준비";
            userSession.m_nUserData[0] = (int)LobbyUserState.NotReady;
        }

        NetGameManager.instance.RoomUserDataUpdate(userSession);
        
		foreach (var t in NetGameManager.instance.m_roomSession.m_userList)
        {
            RoomOneUserAdd(t);
        }
	}
    public void RoomUserAdd(UserSession user)
	{
        //기존 유저들에게 새로운 유저 들어옴 알림
        //RoomUpdate도 실행됨

        if (!CanEnterRoom(user.m_szUserID)) return;
        RoomOneUserAdd(user);
	}
    public void RoomOneUserAdd(UserSession user)
    {
        //유저 추가
        if (!CanEnterRoom(user.m_szUserID)) return;

        GameObject newCharacter = Instantiate(playerPrefab);

        for(int i = 0; i < MaxUserAmount; i++)
        {
            if (_characters.Count > i && _characters[i] != null) continue;
            newCharacter.transform.position = positions[i].position;
            newCharacter.transform.rotation = Quaternion.Euler(0, 180, 0);
            newCharacter.TryGetComponent(out Lobby_Player player);
                
            player.Init(user);
            player.ChangeIcon(user.m_nUserData[0]);

            if (_characters.Count <= i) _characters.Add(newCharacter);
            else _characters[i] = newCharacter;
            break;
        }
    }
    public void RoomUserDel(UserSession user)
	{
        //유저 삭제 및 기존 유저 재정렬
        GameObject toDestroy = _characters.FirstOrDefault(character => character!=null && character.name == user.m_szUserID);

        if (toDestroy == null) return;
        string chatText = $"<#4FB7FF><b>알림 : {user.m_szUserID} 님이 퇴장하셨습니다.</b></color>";
        AddChatting(chatText);
            
        int index = _characters.IndexOf(toDestroy);
        if (index < 0) return;

        _characters.RemoveAt(index);
        Destroy(toDestroy);
            
            
        for (int i = index; i < _characters.Count; i++)
        {
            if (_characters[i] != null)
            {
                _characters[i].transform.position = positions[i].position;
            }
        }

        if (user.m_nUserData[0] != (int)LobbyUserState.Admin) return;
        List<UserSession> userList = NetGameManager.instance.m_roomSession.m_userList;

        if (userList.Count <= 0) return;
        UserSession newAdmin = userList[0];
        newAdmin.m_nUserData[0] = (int)LobbyUserState.Admin;
        NetGameManager.instance.RoomUserDataUpdate(newAdmin);
    }
    

    public void RoomUpdate()
    {
        //룸 정보 업데이트 (새로운 유저 들어왔을 때 자동 실행됨)
    }
    public void RoomUserDataUpdate(UserSession user)
    {
        //NetGameManager에서 RoomUserDataUpdate사용하면 호출됨

        GameObject character = _characters.FirstOrDefault(character =>character!=null && character.name == user.m_szUserID);
        if(character == null) return;
        
        character.TryGetComponent<Lobby_Player>(out var toUpdate);
        toUpdate.ChangeIcon(user.m_nUserData[0]);

        if (user.m_nUserData[0] == (int)LobbyUserState.Admin && user.m_szUserID == _userId)
        {
            lobbyButtonText.text = "게임 시작";
        }
    }
    

    #region 브로드 캐스팅

    private void GameStart()
    {
        UserSession userSession = NetGameManager.instance.GetRoomUserSession(
            NetGameManager.instance.m_userHandle.m_szUserID);

        var data = new GAME_CHAT
        {
            USER = userSession.m_szUserID,
            DATA = 1,
        };

        string sendData = LitJson.JsonMapper.ToJson(data);
        NetGameManager.instance.RoomBroadcast(sendData);
    }

    public void RoomBroadcast(string szData)
    {
        //모든 유저에게 정보 전달

        LitJson.JsonData jData = LitJson.JsonMapper.ToObject(szData);
        string userID = jData["USER"].ToString();
        int dataID = Convert.ToInt32(jData["DATA"].ToString());


        switch (dataID)
        {
            case 1:
                LoadingSceneManager.LoadScene("Game Scene");
                break;
            case 3:
                var spawnedText = Instantiate(chatPrefab, chatViewParent.transform, false);
                spawnedText.text = jData["CHAT"].ToString();
                chatBox.gameObject.SetActive(true);
                break;
        }
    }
    
    

    #endregion

    #region 로그인
    public void OnConnectFail()
    {
        loginAlertText.text = "서버와의 연결에 실패했습니다.";
        MKWNetwork.instance.Disconnect();
    }

    public void OnConnectSuccess()
    {
        loginAlertText.text = "서버 접속에 성공했습니다!";
    }

    public void OnNetConnectDisconnect()
    {
        loginAlertText.text = "서버와의 연결이 끊어졌습니다.";
        MKWNetwork.instance.Disconnect();
    }
    private bool CanEnterRoom(string userID)
    {
        RoomSession roomSession = NetGameManager.instance.m_roomSession;
        int userCount = roomSession.m_userList.Count;
        GameObject toDestroy = GameObject.Find(userID);

        if (toDestroy != null)
        {
            NetGameManager.instance.RoomUserForcedOut(userID);
            return false;
        }

        if (userCount <= MaxUserAmount) return true;
        NetGameManager.instance.RoomUserForcedOut(userID);
        return false;

    }

    public void OnClick_ReconnectButton()
    {
        LoadingSceneManager.LoadScene("lobby Scene");
        TcpHelper.Instance.IsRunning = true;
    }
   
    private void OnClick_QuitGame()
    {
        Application.Quit();
        Debug.LogWarning("종료되는지 확인용");
    }
    public void OnClick_Login()
    {
        //로그인 버튼 클릭시
        _userId = inputUserID.text;
        if (_userId.Length is < 1 or > 10)
        {
            loginAlertText.text = "아이디의 길이를 1자 이상, 10자 이하로 맞춰주세요";
            return;
        }
        if (!_regex.IsMatch(_userId))
        {
            loginAlertText.text = "아이디에 특수문자는 사용할 수 없습니다.";
            return;
        }
        NetGameManager.instance.UserLogin(_userId, 1);
        Debug.LogWarning("클릭이벤트 발생 확인용");
    }

    public void UserLoginResult(ushort usResult)
    {
        switch (usResult)
        {
            //로그인 결과
            case 0:
                loginPanel.SetActive(false);
                break;
            case 125:
                loginAlertText.text = "이미 존재하는 아이디입니다.";
                break;
            default:
                loginAlertText.text = "로그인에 실패했습니다." + usResult;
                break;
        }
    }
    

    #endregion

    #region 채팅

    private void AddChatting(string text)
    {
        var spawnedText = Instantiate(chatPrefab, chatViewParent.transform, false);
        spawnedText.text = text;
    }

    private void SendChatting(string text)
    {
        if (string.IsNullOrWhiteSpace(inputChat.text)) return;

        if (!Input.GetKeyDown(KeyCode.Return) && !Input.GetKeyDown(KeyCode.KeypadEnter)) return;
        string chatText = $"{_userId} : {text}";
        BroadcastChat(chatText);
        inputChat.text = "";
        inputChat.ActivateInputField();
    }

    private void BroadcastChat(string chat)
    {
        UserSession userSession = NetGameManager.instance.GetRoomUserSession(
            NetGameManager.instance.m_userHandle.m_szUserID);

        var data = new GAME_CHAT
        {
            USER = userSession.m_szUserID,
            DATA = 3,
            CHAT = chat,
        };

        string sendData = LitJson.JsonMapper.ToJson(data);
        NetGameManager.instance.RoomBroadcast(sendData);
    }

    #endregion
    
    #region 해당씬에서 안쓰는 거
    
    public void RoomUserMoveDirect(UserSession user)
    {
    }
    public void RoomUserItemUpdate(UserSession user)
    {
    }

    #endregion
}
