namespace lumibelle.Services.Assets;

public static partial class ReelSpeech
{
    // Upstream capability statement, checked 2026-09-18; not checkpoint-level QA.
    // The passages below are authored suggestions, not timed or phonetically balanced recordings.
    public static IReadOnlyList<ReelSpeechLanguage> Languages { get; } = Array.AsReadOnly<ReelSpeechLanguage>([
        new("ar", "Arabic", "العربية", true), new("zh", "Chinese", "中文", true),
        new("en", "English", "English", true), new("fr", "French", "Français", true),
        new("de", "German", "Deutsch", true), new("it", "Italian", "Italiano", true),
        new("ja", "Japanese", "日本語", true), new("ko", "Korean", "한국어", true),
        new("pt", "Portuguese", "Português", true), new("ru", "Russian", "Русский", true),
        new("es", "Spanish", "Español", true), new("sv", "Swedish", "Svenska", false)
    ]);

    // Version 1; ordered Short (~5s), Medium (~8s), Long (~10s), Extended (~15s).
    // Keep this table private. Suggestions return immutable strings, never editable shared arrays.
    private static readonly IReadOnlyDictionary<string, string[]> Passages = new Dictionary<string, string[]>
    {
        ["en"] = [
            "There you are. Ready? Good—let's give this a try.",
            "Oh, there you are. Is everything ready? Good—take your time. We'll work it out together.",
            "Oh, there you are. Is everything ready? Good—take your time. That was unexpected, but I think we're nearly there.",
            "Oh, there you are. Is everything ready? Good—take your time. I thought we'd missed our chance, but this might actually work. Let's check the last few details, and then we'll begin."
        ],
        ["fr"] = [
            "Ah, te voilà. Tout est prêt ? Bien, on essaie.",
            "Ah, te voilà. Tout est prêt ? Bien, prends ton temps. On va trouver une solution ensemble.",
            "Ah, te voilà. Tout est prêt ? Bien, prends ton temps. Je ne m'attendais pas à ça, mais on y est presque.",
            "Ah, te voilà. Tout est prêt ? Bien, prends ton temps. Je pensais qu'on avait raté notre chance, mais ça pourrait marcher. Vérifions les derniers détails, et puis on commence."
        ],
        ["sv"] = [
            "Där är du. Är du redo? Bra, då provar vi.",
            "Jaha, där är du. Är allt klart? Bra, ta det lugnt. Vi löser det här tillsammans.",
            "Jaha, där är du. Är allt klart? Bra, ta det lugnt. Det där var oväntat, men vi är nästan färdiga.",
            "Jaha, där är du. Är allt klart? Bra, ta det lugnt. Jag trodde att vi hade missat vår chans, men det här kan faktiskt fungera. Vi kollar de sista detaljerna, och sedan börjar vi."
        ],
        ["ar"] = [
            "أهلًا بك. هل كل شيء جاهز؟ جيد، لنجرّب.",
            "أهلًا بك. هل كل شيء جاهز؟ جيد، لا داعي للعجلة. سنجد الحل معًا.",
            "أهلًا بك. هل كل شيء جاهز؟ جيد، لا داعي للعجلة. لم أتوقع ذلك، لكننا أوشكنا على الانتهاء.",
            "أهلًا بك. هل كل شيء جاهز؟ جيد، لا داعي للعجلة. ظننت أننا أضعنا الفرصة، لكن هذه الفكرة قد تنجح. لنتأكد من التفاصيل الأخيرة، ثم نبدأ بهدوء."
        ],
        ["zh"] = [
            "你来啦。准备好了吗？好，我们试试看。",
            "你来啦。都准备好了吗？好，不用着急。我们一起想办法。",
            "你来啦。都准备好了吗？好，慢慢来。刚才真没想到，不过我们快完成了。",
            "你来啦。都准备好了吗？好，慢慢来。我还以为错过机会了，没想到这个办法真的有用。我们再检查一下最后几个细节，然后就开始吧。"
        ],
        ["de"] = [
            "Da bist du ja. Bereit? Gut, probieren wir es aus.",
            "Ach, da bist du ja. Ist alles bereit? Gut, lass dir Zeit. Wir schaffen das zusammen.",
            "Ach, da bist du ja. Ist alles bereit? Gut, lass dir Zeit. Das war unerwartet, aber wir sind fast fertig.",
            "Ach, da bist du ja. Ist alles bereit? Gut, lass dir Zeit. Ich dachte, wir hätten unsere Chance verpasst, aber das könnte funktionieren. Prüfen wir noch die letzten Details, dann legen wir los."
        ],
        ["it"] = [
            "Ah, eccoti. È tutto pronto? Bene, facciamo una prova.",
            "Ah, eccoti. È tutto pronto? Bene, fai con calma. Troveremo una soluzione insieme.",
            "Ah, eccoti. È tutto pronto? Bene, fai con calma. Non me l'aspettavo, ma credo che ci siamo quasi.",
            "Ah, eccoti. È tutto pronto? Bene, fai con calma. Pensavo che avessimo perso l'occasione, ma questa idea potrebbe funzionare. Controlliamo gli ultimi dettagli e poi cominciamo."
        ],
        ["ja"] = [
            "準備はいいですか？大丈夫、やってみましょう。",
            "あ、来たんですね。準備はいいですか？大丈夫、一緒にやってみましょう。",
            "あ、来たんですね。準備はいいですか？大丈夫、急がなくていいですよ。もう少しでできそうです。",
            "あ、来たんですね。準備はいいですか？大丈夫、急がなくていいですよ。もう間に合わないかと思いましたが、これならうまくいきそうです。最後にもう一度確かめて、それから始めましょう。"
        ],
        ["ko"] = [
            "왔네요. 준비됐어요? 좋아요, 한번 해 봐요.",
            "아, 왔네요. 준비는 다 됐어요? 좋아요, 천천히 해요. 같이 방법을 찾아봐요.",
            "아, 왔네요. 준비는 다 됐어요? 좋아요, 천천히 해요. 조금 놀랐지만, 이제 거의 다 됐어요.",
            "아, 왔네요. 준비는 다 됐어요? 좋아요, 천천히 해요. 기회를 놓친 줄 알았는데, 이 방법이면 될 것 같아요. 마지막으로 몇 가지만 확인하고, 그다음에 시작해요."
        ],
        ["pt"] = [
            "Ah, você chegou. Tudo pronto? Ótimo, vamos tentar.",
            "Ah, você chegou. Está tudo pronto? Ótimo, sem pressa. Vamos encontrar uma solução juntos.",
            "Ah, você chegou. Está tudo pronto? Ótimo, sem pressa. Não esperava por isso, mas acho que estamos quase lá.",
            "Ah, você chegou. Está tudo pronto? Ótimo, sem pressa. Achei que tínhamos perdido a chance, mas isso pode funcionar. Vamos conferir os últimos detalhes e depois começamos."
        ],
        ["ru"] = [
            "А, вот и ты. Всё готово? Хорошо, давай попробуем.",
            "А, вот и ты. Всё готово? Хорошо, не спеши. Мы обязательно разберёмся вместе.",
            "А, вот и ты. Всё готово? Хорошо, не спеши. Это было неожиданно, но, кажется, мы почти закончили.",
            "А, вот и ты. Всё готово? Хорошо, не спеши. Казалось, что мы упустили свой шанс, но этот способ может сработать. Давай проверим последние детали, а потом начнём."
        ],
        ["es"] = [
            "Ah, ya llegaste. ¿Todo listo? Bien, vamos a probar.",
            "Ah, ya llegaste. ¿Está todo listo? Bien, sin prisa. Encontraremos una solución juntos.",
            "Ah, ya llegaste. ¿Está todo listo? Bien, sin prisa. No esperaba eso, pero creo que ya casi terminamos.",
            "Ah, ya llegaste. ¿Está todo listo? Bien, sin prisa. Pensé que habíamos perdido la oportunidad, pero esto podría funcionar. Revisemos los últimos detalles y después empezamos."
        ]
    };
}
