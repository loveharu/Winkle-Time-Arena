using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;
using Data;
using UnityEngine.UI;
using DG.Tweening;
using TMPro;
using Unity.Mathematics;
using UnityEngine.AI;
using DamageNumbersPro;

public enum EnemyType
{
    Boss,
    Monster,
    RedSpider,
    GreenSpider
}
public class EnemyHealth : MonoBehaviour
{
    private NavMeshAgent _nav;
    public EnemyHealthBar enemyHealthBar;
    public float maxHealth;
    public float currentHealth;
    public Animator anim;
    public EnemyType enemyType = EnemyType.Monster;
    public TMP_Text deathText;
    public Image EndingImage;
    
    private static readonly int DoDie = Animator.StringToHash("doDie");
    public DamageNumber damageNumbersPrefab;
    private string currentSceneName;
    public Transform hudPos;
    public ParticleSystem dieEffect;
    public bool isDead = false;
    public GameObject playerCount;
    
    void Start()
    {
        _nav = GetComponent<NavMeshAgent>();
        playerCount = GameObject.FindGameObjectWithTag("Manager");
        StartCoroutine(DelayedStart());
        
    }
    
    IEnumerator DelayedStart()
    {
        yield return new WaitForSeconds(0.1f);
        
        EnemyHealthBase();
    }

    
    public void EnemyHealthBase()
    {
        int childCount = playerCount.transform.childCount;

        switch (childCount)
        {
            case 1:
            {
                string json = enemyType switch
                {
                    EnemyType.Monster => "{\"EnemyHealth\": 1500, \"Health\": 1500}",
                    EnemyType.RedSpider => "{\"EnemyHealth\": 1600, \"Health\": 1600}",
                    EnemyType.GreenSpider => "{\"EnemyHealth\": 1800, \"Health\": 1800}",
                    EnemyType.Boss => "{\"EnemyHealth\": 20000, \"Health\": 20000}",
                    _ => ""
                };

                EnemyStat enemyStat1 = JsonConvert.DeserializeObject<EnemyStat>(json);
                maxHealth = (int)enemyStat1.EnemyHealth;
                currentHealth = (int)enemyStat1.Health;
                currentHealth = maxHealth;
                if (enemyHealthBar != null && enemyType != EnemyType.Boss)
                    enemyHealthBar.UpdateHealth();
                if(enemyHealthBar!= null && enemyType == EnemyType.Boss)
                    enemyHealthBar.UpdateBossHealth();
                DOTween.SetTweensCapacity(500, 50);
                break;
            }
            case 2:
            case 3:
            {
                string json = enemyType switch
                {
                    EnemyType.Monster => "{\"EnemyHealth\": 3000, \"Health\": 3000}",
                    EnemyType.RedSpider => "{\"EnemyHealth\": 3200, \"Health\": 3200}",
                    EnemyType.GreenSpider => "{\"EnemyHealth\": 3600, \"Health\":3600}",
                    EnemyType.Boss => "{\"EnemyHealth\": 30000, \"Health\": 30000}",
                    _ => ""
                };

                EnemyStat enemyStat1 = JsonConvert.DeserializeObject<EnemyStat>(json);

                maxHealth = (int)enemyStat1.EnemyHealth;
                currentHealth = (int)enemyStat1.Health;
                currentHealth = maxHealth;
                if (enemyHealthBar != null && enemyType != EnemyType.Boss)
                    enemyHealthBar.UpdateHealth();
                if(enemyHealthBar!= null && enemyType == EnemyType.Boss)
                    enemyHealthBar.UpdateBossHealth();
            
                DOTween.SetTweensCapacity(500, 50);
                break;
            }
            case 4:
            case 5:
            {
                string json = enemyType switch
                {
                    EnemyType.Monster => "{\"EnemyHealth\": 6000, \"Health\": 6000}",
                    EnemyType.RedSpider => "{\"EnemyHealth\": 5500, \"Health\": 5500}",
                    EnemyType.GreenSpider => "{\"EnemyHealth\": 8000, \"Health\":8000}",
                    EnemyType.Boss => "{\"EnemyHealth\": 50000, \"Health\": 50000}",
                    _ => ""
                };

                EnemyStat enemyStat1 = JsonConvert.DeserializeObject<EnemyStat>(json);
                maxHealth = (int)enemyStat1.EnemyHealth;
                currentHealth = (int)enemyStat1.Health;
                currentHealth = maxHealth;
                if (enemyHealthBar != null && enemyType != EnemyType.Boss)
                    enemyHealthBar.UpdateHealth();
                if(enemyHealthBar!= null && enemyType == EnemyType.Boss)
                    enemyHealthBar.UpdateBossHealth();
                DOTween.SetTweensCapacity(500, 50);
                break;
            }
        }
    }
    public void TakeDamage(int damage, bool isNetwork = true)
    {
        if (isDead) return;
        
        if ((isNetwork && MultiScene.Instance.isMasterClient) || !isNetwork || enemyType == EnemyType.Boss)
        {
            ApplyDamage(damage, isNetwork);
        }
    }

    private void ApplyDamage(int damage, bool isNetwork)
    {
        float realDamage = Math.Min(damage, currentHealth);
        currentHealth = Math.Max(currentHealth - damage, 0);
        if (enemyHealthBar != null)
        {
            switch (enemyType)
            {
                case EnemyType.Boss:
                    enemyHealthBar.UpdateBossHealth();
                    break;
                case EnemyType.Monster:
                case EnemyType.RedSpider:
                case EnemyType.GreenSpider:
                    enemyHealthBar.UpdateHealth();
                    break;
            }
        }

        if (damageNumbersPrefab != null)
        {
            damageNumbersPrefab.Spawn(hudPos.transform.position, realDamage);
        }
        
        if (isNetwork)
        {
            if (enemyType == EnemyType.Boss)
            {
                MultiScene.Instance.BroadCastingEnemyTakeDamage(MultiScene.Instance.enemyList.IndexOf(this.gameObject),
                    damage, true);
            }
            else
            {
                MultiScene.Instance.BroadCastingEnemyTakeDamage(MultiScene.Instance.enemyList.IndexOf(this.gameObject),
                    damage);
            }
        }

        if (!(currentHealth <= 0)) return;
        Die();
        if (enemyType == EnemyType.Boss)
        {
        }
    }
    void Die()
    {
        isDead = true;
        anim.SetTrigger(DoDie);
        MultiScene.Instance.BroadCastingEnemyAnimation(MultiScene.Instance.enemyList.IndexOf(gameObject), DoDie, true);
    }
    void BeginDeath()
    {
        bool isEnemy = gameObject.TryGetComponent(out MultiEnemy enemy);
        bool isBoss = gameObject.TryGetComponent(out MultiBoss boss);
        _nav.isStopped = true;
        
        if (isEnemy)
        {
            enemy.isDead = true;
            dieEffect.Play();
        }
        else if (isBoss)
        {
            boss.isDead = true;
            boss.Stop();
        }
    }

    void EndDeath()
    {
        if (MultiScene.Instance.isMasterClient)
        {
            var index = MultiScene.Instance.GetRandomInt(3);
            var newItem = Instantiate(MultiScene.Instance.itemPrefabs[index], transform.position, quaternion.identity);
            newItem.transform.SetParent(MultiScene.Instance.itemListParent);
            MultiScene.Instance.itemsList.Add(newItem);
            MultiScene.Instance.BroadCastingEnemyItem(transform.position, index);
        }

        MultiScene.Instance.enemyList.Remove(gameObject);
        ReIndexing();
        Destroy(gameObject);
    }

    private void ReIndexing()
    {
        var enemies = MultiScene.Instance.enemyList;

        foreach (var enemy in enemies.Where(enemy => enemy != null))
        {
            enemy.TryGetComponent(out MultiEnemy multiEnemy);
            if(multiEnemy == null) continue;
            multiEnemy.SetIndex();
        }
    }
    
    void BossDeath()
    {
        deathText.DOText("축하합니다 당신은 " + "<color=red>" + "보스" + "</color>" + "를 잡았습니다!", 3, true, ScrambleMode.None, null);
        EndingImage.rectTransform.gameObject.SetActive(true);
    }
}
