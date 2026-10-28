using System.Globalization;
namespace BatteryDiagnostic;

internal enum UiLanguage { French, English, Russian }
internal static class L
{
    internal static CultureInfo Culture(UiLanguage lang) => CultureInfo.GetCultureInfo(lang switch { UiLanguage.French => "fr-FR", UiLanguage.English => "en-GB", _ => "ru-RU" });
    internal static string Code(UiLanguage lang) => lang switch { UiLanguage.French => "fr", UiLanguage.English => "en", _ => "ru" };
    internal static string Name(UiLanguage lang) => lang switch { UiLanguage.French => "Français", UiLanguage.English => "English", _ => "Русский" };
    internal static string Get(UiLanguage lang, string key)
    {
        var t = Text[key];
        return lang switch { UiLanguage.French => t.Fr, UiLanguage.English => t.En, _ => t.Ru };
    }
    internal static string F(UiLanguage lang, string key, params object[] args) => string.Format(Culture(lang), Get(lang, key), args);
    internal static string Percent(UiLanguage lang, double value, string format="0.#") => value.ToString(format,Culture(lang))+(lang==UiLanguage.English?"%":"\u00a0%");
    internal static void ApplyCulture(UiLanguage lang)
    {
        var culture=Culture(lang);
        CultureInfo.CurrentCulture=CultureInfo.CurrentUICulture=culture;
        CultureInfo.DefaultThreadCurrentCulture=CultureInfo.DefaultThreadCurrentUICulture=culture;
    }
    internal static void Validate()
    {
        foreach (var (key, values) in Text)
        {
            var translations=new[]{values.Fr,values.En,values.Ru};
            if(translations.Any(string.IsNullOrWhiteSpace))throw new Exception("Missing translation: "+key);
            string Slots(string text)=>string.Join(",",System.Text.RegularExpressions.Regex.Matches(text,@"\{(\d+)(?:[^}]*)\}").Select(m=>m.Groups[1].Value).Order());
            if(translations.Any(t=>Slots(t)!=Slots(values.Fr)))throw new Exception("Translation placeholders differ: "+key);
            foreach(var text in translations) _=string.Format(Culture(UiLanguage.French),text,Enumerable.Repeat<object>("test",16).ToArray());
        }
    }
    static readonly Dictionary<string, (string Fr, string En, string Ru)> Text = new()
    {
        ["subtitle"] = ("Portable 0.5.18 · Mise à jour automatique · Hors ligne", "Portable 0.5.18 · Automatic updates · Offline", "Portable 0.5.18 · Автообновление · Без сети"),
        ["information"] = ("Informations sur la batterie", "Battery information", "Информация о батарее"),
        ["system"] = ("Système", "System", "Система"),
        ["battery"] = ("Batterie {0}", "Battery {0}", "Батарея {0}"),
        ["declared"] = ("Capacité nominale", "Design capacity", "Заявленная ёмкость"),
        ["full"] = ("Capacité à pleine charge", "Full-charge capacity", "Ёмкость при полном заряде"),
        ["health"] = ("État de la batterie", "Battery condition", "Состояние аккумулятора"),
        ["gradeCritical"] = ("Critique", "Critical", "Критическое"),
        ["gradeMedium"] = ("Moyen", "Moderate", "Среднее"),
        ["gradePerfect"] = ("Parfait", "Excellent", "Отличное"),
        ["unclassified"] = ("Non classé", "Unclassified", "Не классифицировано"),
        ["charge"] = ("Statut de charge", "Charge status", "Состояние заряда"),
        ["retained"] = ("{0} de capacité conservée", "{0} capacity retained", "Сохранено {0} ёмкости"),
        ["unknown"] = ("Aucune donnée", "No data", "Нет данных"),
        ["waiting"] = ("Lecture des données…", "Reading data…", "Получение данных…"),
        ["sourceWindows"] = ("Pourcentage Windows (système)", "Windows percentage (system)", "Процент Windows (система)"),
        ["sourceBattery"] = ("Pourcentage calculé pour cette batterie", "Percentage calculated for this battery", "Процент рассчитан для этой батареи"),
        ["sourceUnknown"] = ("Pourcentage indisponible", "Percentage unavailable", "Процент недоступен"),
        ["online"] = ("Par alimentation", "On AC power", "От сети"),
        ["offline"] = ("Sur batterie", "On battery power", "От аккумулятора"),
        ["powerUnknown"] = ("Aucune donnée", "No data", "Нет данных"),
        ["charging"] = ("En charge", "Charging", "Заряжается"),
        ["connectedIdle"] = ("Branchée, pas en charge", "Plugged in, not charging", "Подключена, не заряжается"),
        ["discharging"] = ("En décharge", "Discharging", "Разряжается"),
        ["idle"] = ("Charge inactive", "Not charging", "Не заряжается"),
        ["critical"] = ("Signal critique", "Critical signal", "Критический сигнал"),
        ["conflict"] = ("État de charge incohérent", "Inconsistent charging state", "Несогласованное состояние зарядки"),
        ["manufacturer"] = ("Fabricant", "Manufacturer", "Производитель"),
        ["capacityHeading"] = ("Capacité de la batterie", "Battery capacity", "Ёмкость аккумулятора"),
        ["batteryInfoHeading"] = ("Informations sur la batterie", "Battery information", "Информация об аккумуляторе"),
        ["batteryModel"] = ("Nom / modèle de batterie", "Battery name / model", "Название / модель батареи"),
        ["batteryModelTip"] = ("Nom communiqué par le contrôleur de la batterie via Windows. Aucune base externe ni correspondance supposée ; si cette information n’est pas fournie, aucune donnée ne s’affiche.", "Name reported by the battery controller through Windows. No external lookup or guessed match; if the controller does not provide it, no data is shown.", "Имя, сообщаемое контроллером батареи через Windows. Внешний каталог и догадки не используются; если контроллер не предоставляет это значение, показывается «нет данных»."),
        ["model"] = ("Ordinateur", "Computer", "Ноутбук"),
        ["more"] = ("Plus d’infos", "More information", "Подробнее"),
        ["less"] = ("Moins d’infos", "Less information", "Скрыть подробности"),
        ["save"] = ("Enregistrer le rapport", "Save report", "Сохранить отчёт"),
        ["htmlReport"] = ("Rapport HTML (*.html)", "HTML report (*.html)", "Отчёт HTML (*.html)"),
        ["powerState"] = ("Indicateurs d’alimentation (PowerState)", "Power flags (PowerState)", "Флаги питания (PowerState)"),
        ["saved"] = ("Rapports HTML et CSV enregistrés : {0}", "HTML and CSV reports saved: {0}", "Отчёты HTML и CSV сохранены: {0}"),
        ["errorSave"] = ("Erreur d’enregistrement", "Unable to save report", "Ошибка сохранения"),
        ["updated"] = ("Mis à jour à {0} · Actualisation toutes les 2 s", "Updated at {0} · Refreshes every 2 s", "Обновлено в {0} · Обновление каждые 2 с"),
        ["errorRead"] = ("Données indisponibles. Nouvelle tentative automatique.", "Data unavailable. Retrying automatically.", "Данные недоступны. Повторяем запрос автоматически."),
        ["timeout"] = ("La lecture prend trop de temps.", "The read is taking too long.", "Чтение занимает слишком много времени."),
        ["noBattery"] = ("Aucune batterie accessible détectée", "No accessible battery detected", "Доступная батарея не обнаружена"),
        ["healthTip"] = ("Calcul : {2} conservés\r\nCapacité actuelle / nominale : {0} / {1}\r\nÉchelle : Critique 1–39 %, Moyen 40–79 %\r\nParfait 80–100 % ; hors plage : non classé.\r\nValeurs du contrôleur ; état des cellules non mesuré.", "Calculated condition: {2} retained\r\nCurrent / design capacity: {0} / {1}\r\nScale: Critical 1–39%, Moderate 40–79%\r\nExcellent 80–100%; otherwise unclassified.\r\nController-reported values; cell condition not measured.", "Расчётное состояние: сохранено {2}\r\nТекущая / заявленная ёмкость: {0} / {1}\r\nШкала: критическое 1–39%, среднее 40–79%\r\nотличное 80–100%; вне диапазонов — без оценки.\r\nДанные контроллера; состояние ячеек не измеряется."),
        ["healthUnknown"] = ("Capacités absentes ou incohérentes : état non calculé.", "Missing or inconsistent capacities: condition not calculated.", "Ёмкости отсутствуют или несогласованы: состояние не рассчитано."),
        ["chargeTip"] = ("{0}\n{1}\n{2}", "{0}\n{1}\n{2}", "{0}\n{1}\n{2}"),
        ["capacityMismatch"] = ("La capacité restante dépasse la capacité à pleine charge déclarée. Valeurs du contrôleur non concordantes ; ce seul écart ne prouve pas une panne.", "Remaining capacity exceeds the reported full-charge capacity. Controller values disagree; this alone does not prove a battery fault.", "Оставшаяся ёмкость превышает сообщённую полную. Показания контроллера не согласуются; это расхождение само по себе не доказывает неисправность."),
        ["invalidCapacity"] = ("Capacités nulles ou incohérentes : estimation de l’état indisponible.", "Zero or inconsistent capacities: condition estimate unavailable.", "Нулевые или несогласованные ёмкости: оценка состояния недоступна."),
        ["aboveDesign"] = ("La capacité actuelle dépasse la capacité nominale ; aucune catégorie n’est attribuée.", "Current capacity exceeds design capacity; no category is assigned.", "Текущая ёмкость выше заявленной; категория не присваивается."),
        ["zeroVoltage"] = ("Tension nulle : mesure non fiable.", "Zero voltage: unreliable reading.", "Нулевое напряжение: недостоверное показание."),
        ["criticalSignal"] = ("Signal BATTERY_CRITICAL : branchez le secteur.", "BATTERY_CRITICAL signal: connect AC power.", "Сигнал BATTERY_CRITICAL: подключите питание."),
        ["stateConflict"] = ("Charge et décharge signalées simultanément.", "Charging and discharging flags are set simultaneously.", "Одновременно установлены флаги зарядки и разрядки."),
        ["hot"] = ("Température ≥50 °C : vérifiez le refroidissement. Seuil de prudence, non constructeur.", "Temperature ≥50 °C: check cooling. Precautionary threshold, not an OEM criterion.", "Температура ≥50 °C: проверьте охлаждение. Это порог осторожности, не критерий производителя."),
        ["sourceError"] = ("Erreur de source : {0}", "Source error: {0}", "Ошибка источника: {0}"),
        ["scope"] = ("Estimation selon les capacités déclarées. État des cellules et autotest constructeur : aucune donnée.", "Estimate based on reported capacities. Cell condition and OEM self-test: no data.", "Оценка по сообщённым ёмкостям. Состояние ячеек и фирменный самотест: нет данных."),
        ["quality"] = ("Remarque sur les données", "Data note", "Примечание к данным"),
        ["signals"] = ("Signaux à vérifier", "Signals to check", "Сигналы, требующие проверки"),
        ["report"] = ("Rapport sur la batterie", "Battery report", "Отчёт о батарее"),
        ["time"] = ("Heure de mise à jour", "Update time", "Время обновления"),
        ["power"] = ("Alimentation", "Power source", "Питание"),
        ["source"] = ("Source du pourcentage", "Percentage source", "Источник процента"),
        ["rawRemaining"] = ("Capacité restante déclarée", "Reported remaining capacity", "Сообщённая оставшаяся ёмкость"),
        ["voltage"] = ("Tension", "Voltage", "Напряжение"),
        ["rate"] = ("Puissance (+charge / −décharge)", "Power (+charge / −discharge)", "Мощность (+заряд / −разряд)"),
        ["temperature"] = ("Température", "Temperature", "Температура"),
        ["cycles"] = ("Cycles (0 non interprété)", "Cycles (0 not interpreted)", "Циклы (0 не интерпретируется)"),
        ["relative"] = ("unités relatives", "relative units", "относительные единицы"),
        ["maximize"] = ("Agrandir", "Maximize", "Развернуть"),
        ["restore"] = ("Restaurer", "Restore window", "Обычное окно"),
        ["minimize"] = ("Réduire", "Minimize", "Свернуть"),
        ["close"] = ("Fermer", "Close", "Закрыть"),
        ["language"] = ("Langue", "Language", "Язык"),
        ["selection"] = ("Choisir la batterie", "Select battery", "Выбрать батарею"),
        
        ["observation"] = ("Lectures : {0} · Durée : {1} s", "Readings: {0} · Duration: {1} s", "Чтения: {0} · Длительность: {1} с"),
        
        ["observationUnknown"] = ("Observation : aucune donnée", "Observation: no data", "Наблюдение: нет данных"),
        ["stabilityUnknown"] = ("Stabilité : données insuffisantes", "Stability: insufficient data", "Стабильность: недостаточно данных"),
        ["stabilityClear"] = ("Aucun écart détecté ; panne non exclue", "No discrepancy detected; fault not ruled out", "Расхождений не выявлено; дефект не исключён"),
        ["stabilityNotes"] = ("Remarques à vérifier : {0}", "Notes to investigate: {0}", "Замечаний для проверки: {0}"),
        ["passive"] = ("Contrôle passif des données du contrôleur. Aucun test de charge ni autotest constructeur. L’absence de signal ne confirme pas le bon fonctionnement.", "Passive check of controller reports. No load test or OEM self-test. Absence of a signal does not establish that the battery is fault-free.", "Пассивная проверка показаний контроллера. Без нагрузочного и фирменного теста. Отсутствие сигнала не подтверждает исправность."),
        ["shortObservation"] = ("Observation trop courte : stabilité non évaluée (minimum 30 s et 10 lectures exploitables).", "Observation too short: stability not assessed (minimum 30 s and 10 usable readings).", "Наблюдение слишком короткое: стабильность не оценена (минимум 30 с и 10 пригодных чтений)."),
        ["noObservedSignal"] = ("Aucun écart détecté par les contrôles disponibles pendant cette observation ; ce n’est pas une preuve d’absence de panne.", "No discrepancy detected by available checks during this observation; this is not proof that the battery is fault-free.", "За время наблюдения доступные проверки не выявили расхождений; это не доказательство отсутствия неисправности."),
        ["jumpCharge"] = ("Variation de charge ≥5 points en ≤10 s, alimentation et état inchangés : vérifier le contrôleur ou son étalonnage ; panne non confirmée.", "Charge changed ≥5 percentage points within ≤10 s with unchanged power and charging state: check controller reporting or calibration; fault not confirmed.", "Заряд изменился на ≥5 п. п. за ≤10 с при прежнем питании и состоянии зарядки: проверьте показания или калибровку; неисправность не подтверждена."),
        ["jumpFull"] = ("Capacité à pleine charge modifiée de >5 % en ≤10 s : estimation du contrôleur instable ; panne non confirmée.", "Full-charge capacity changed by >5% within ≤10 s: controller estimate unstable; fault not confirmed.", "Полная ёмкость изменилась более чем на 5% за ≤10 с: оценка контроллера нестабильна; неисправность не подтверждена."),
        ["percentDisagreement"] = ("Écart >5 points entre Windows et capacité restante / pleine. Ces sources ne sont pas des mesures indépendantes.", "Windows and remaining/full capacity differ by >5 percentage points. These sources are not independent measurements.", "Процент Windows и отношение оставшейся/полной ёмкости расходятся более чем на 5 п. п. Источники не являются независимыми измерениями."),
        ["rateConflict"] = ("Sens de puissance incohérent avec les indicateurs charge/décharge pendant ≥3 lectures sur ≥4 s (>100 mW). Vérifier les données ; panne non confirmée.", "Power direction contradicts charge/discharge flags for ≥3 readings over ≥4 s (>100 mW). Check reporting; fault not confirmed.", "Знак мощности противоречит флагам зарядки/разрядки ≥3 чтения за ≥4 с (>100 мВт). Проверьте данные; неисправность не подтверждена."),
        ["moreFindings"] = (" · {0} autres remarques (survoler)", " · {0} more notes (hover)", " · ещё {0} замечаний (наведите курсор)"),
        ["live"] = ("Données actualisées automatiquement", "Automatically updated readings", "Автоматически обновляемые показания"),
    };
}
