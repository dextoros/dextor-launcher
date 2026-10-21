using System.Collections.Generic;

namespace DexTor.Application;

internal static class ExtendedWizardLocalizations
{
    internal static IReadOnlyDictionary<string, string> Get(string language)
    {
        var source = language switch
        {
            "de" => German,
            "es" => Spanish,
            "fr" => French,
            "pt-BR" => PortugueseBrazil,
            "pl" => Polish,
            "tr" => Turkish,
            "zh-CN" => ChineseSimplified,
            _ => new Dictionary<string, string>()
        };
        var result = new Dictionary<string, string>(source);
        if (UsbSafetyTexts.TryGetValue(language, out var safety))
        {
            result["launcher.wizard.usb.description"] = safety.Description;
            result["launcher.wizard.usb.confirm_erase"] = safety.Confirmation;
        }
        if (VmJourneyTexts.TryGetValue(language, out var vm))
        {
            result["launcher.wizard.vm.checking_usb"] = vm.CheckingUsb;
            result["launcher.wizard.vm.checking_usb_details"] = vm.CheckingUsbDetails;
            result["launcher.wizard.vm.checking_runtime"] = vm.CheckingComponents;
            result["launcher.wizard.vm.checking_runtime_details"] = vm.CheckingComponentsDetails;
            result["launcher.wizard.vm.locking_storage"] = vm.ConnectingStorage;
            result["launcher.wizard.vm.locking_storage_details"] = vm.ConnectingStorageDetails;
            result["launcher.wizard.vm.starting"] = vm.Starting;
            result["launcher.wizard.vm.starting_patience"] = vm.Patience;
            result["launcher.wizard.vm.starting_details"] = vm.StartingDetails;
            result["launcher.wizard.vm.connecting"] = vm.Waiting;
            result["launcher.wizard.vm.connecting_details"] = vm.WaitingDetails;
            result["launcher.wizard.vm.keep_usb"] = vm.KeepUsb;
            result["launcher.wizard.vm.running"] = vm.Running;
            result["launcher.wizard.vm.running_details"] = vm.RunningDetails;
            result["launcher.wizard.vm.stop"] = vm.Stop;
            result["launcher.wizard.vm.stopping"] = vm.Stopping;
            result["launcher.wizard.vm.stopped"] = vm.Stopped;
            result["launcher.wizard.vm.safe_to_eject"] = vm.SafeToEject;
            result["launcher.wizard.vm.usb_removed_title"] = vm.UsbRemovedTitle;
            result["launcher.wizard.vm.usb_removed_message"] = vm.UsbRemovedMessage;
        }

        // The full translation comes last and wins: the compact tables above covered only
        // the step names and headings, so everything added since showed in English.
        foreach (var (key, value) in ReadFullTranslation(language))
        {
            result[key] = value;
        }

        return result;
    }

    /// <summary>Every UI string for <paramref name="language"/>, from Localization\{language}.json.</summary>
    internal static IReadOnlyDictionary<string, string> ReadFullTranslation(string language)
    {
        using var stream = typeof(ExtendedWizardLocalizations).Assembly
            .GetManifestResourceStream($"DexTor.Application.Localization.{language}.json");
        if (stream is null) return new Dictionary<string, string>();
        using var document = System.Text.Json.JsonDocument.Parse(stream);
        var result = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                result[property.Name] = property.Value.GetString()!;
            }
        }

        return result;
    }

    private sealed record VmJourneyText(
        string CheckingUsb,
        string CheckingUsbDetails,
        string CheckingComponents,
        string CheckingComponentsDetails,
        string ConnectingStorage,
        string ConnectingStorageDetails,
        string Starting,
        string Patience,
        string StartingDetails,
        string Waiting,
        string WaitingDetails,
        string KeepUsb,
        string Running,
        string RunningDetails,
        string Stop,
        string Stopping,
        string Stopped,
        string SafeToEject,
        string UsbRemovedTitle,
        string UsbRemovedMessage);

    private static readonly Dictionary<string, VmJourneyText> VmJourneyTexts =
        new Dictionary<string, VmJourneyText>
        {
            ["de"] = new(
                "DexTor-USB wird geprüft", "Wir stellen sicher, dass USB-Laufwerk und privater Speicher verbunden sind.",
                "Startkomponenten werden vorbereitet", "Die zum Start auf diesem Computer benötigten Dateien werden geprüft.",
                "Privater Speicher wird verbunden", "Der private Speicher wird direkt vom USB-Laufwerk geöffnet.",
                "DexTor OS wird gestartet", "Bitte haben Sie etwas Geduld. Der Start des virtuellen Computers kann einige Minuten dauern. Warten Sie, bis DexTor OS in einem neuen Fenster vollständig geladen ist, und lassen Sie das USB-Laufwerk angeschlossen.",
                "Der virtuelle Computer startet. Das DexTor-OS-Fenster wird gleich geöffnet.", "DexTor OS wird geladen", "DexTor OS lädt im neuen Fenster. Der Bildschirm kann kurz dunkel bleiben.",
                "Lassen Sie das USB-Laufwerk verbunden, solange DexTor OS läuft.", "DexTor OS wird ausgeführt", "DexTor OS ist in einem maximierten Fenster geöffnet. Beenden Sie die Sitzung hier, bevor Sie das USB-Laufwerk entfernen.",
                "DexTor OS stoppen", "DexTor OS wird sicher beendet", "DexTor OS wurde beendet", "Die Sitzung ist beendet. Das USB-Laufwerk kann jetzt sicher entfernt werden.",
                "DexTor-USB wurde getrennt", "DexTor OS wurde sofort beendet. Vor dem nächsten Start muss der Speicher möglicherweise geprüft werden."),
            ["es"] = new(
                "Comprobando la unidad USB de DexTor", "Comprobamos que la unidad USB y el almacenamiento privado sigan conectados.",
                "Preparando los componentes de inicio", "Comprobamos los archivos necesarios para abrir DexTor OS en este equipo.",
                "Conectando tu almacenamiento privado", "Abrimos el almacenamiento privado directamente desde la unidad USB.",
                "Iniciando DexTor OS", "Ten paciencia. Iniciar el equipo virtual puede tardar unos minutos. Espera a que DexTor OS termine de cargar en una ventana nueva y mantén conectada la unidad USB.",
                "El equipo virtual se está iniciando. La ventana de DexTor OS se abrirá en breve.", "Esperando a DexTor OS", "DexTor OS se está cargando en la ventana nueva. La pantalla puede permanecer oscura unos instantes.",
                "Mantén conectada la unidad USB mientras se ejecuta DexTor OS.", "DexTor OS está en ejecución", "DexTor OS está abierto en una ventana maximizada. Detén la sesión aquí antes de retirar la unidad USB.",
                "Detener DexTor OS", "Deteniendo DexTor OS de forma segura", "DexTor OS se ha detenido", "La sesión está cerrada. Ya puedes retirar la unidad USB de forma segura.",
                "Se desconectó la unidad USB de DexTor", "DexTor OS se detuvo inmediatamente. Puede que sea necesario comprobar el almacenamiento antes del próximo inicio."),
            ["fr"] = new(
                "Vérification de la clé USB DexTor", "Nous vérifions que la clé USB et le stockage privé sont toujours connectés.",
                "Préparation des composants de démarrage", "Nous vérifions les fichiers nécessaires pour ouvrir DexTor OS sur cet ordinateur.",
                "Connexion de votre stockage privé", "Le stockage privé est ouvert directement depuis la clé USB.",
                "Démarrage de DexTor OS", "Merci de patienter. Le démarrage de l’ordinateur virtuel peut prendre quelques minutes. Attendez le chargement complet de DexTor OS dans une nouvelle fenêtre et laissez la clé USB connectée.",
                "L’ordinateur virtuel démarre. La fenêtre DexTor OS va bientôt s’ouvrir.", "Chargement de DexTor OS", "DexTor OS se charge dans la nouvelle fenêtre. L’écran peut rester sombre quelques instants.",
                "Laissez la clé USB connectée pendant l’utilisation de DexTor OS.", "DexTor OS est en cours d’exécution", "DexTor OS est ouvert dans une fenêtre agrandie. Arrêtez la session ici avant de retirer la clé USB.",
                "Arrêter DexTor OS", "Arrêt sécurisé de DexTor OS", "DexTor OS est arrêté", "La session est fermée. Vous pouvez maintenant retirer la clé USB en toute sécurité.",
                "La clé USB DexTor a été déconnectée", "DexTor OS a été arrêté immédiatement. Une vérification du stockage pourra être nécessaire au prochain démarrage."),
            ["pt-BR"] = new(
                "Verificando a unidade USB DexTor", "Estamos confirmando que a unidade USB e o armazenamento privado continuam conectados.",
                "Preparando os componentes de inicialização", "Estamos verificando os arquivos necessários para abrir o DexTor OS neste computador.",
                "Conectando seu armazenamento privado", "O armazenamento privado está sendo aberto diretamente da unidade USB.",
                "Iniciando o DexTor OS", "Aguarde. A inicialização do computador virtual pode levar alguns minutos. Espere o DexTor OS terminar de carregar em uma nova janela e mantenha a unidade USB conectada.",
                "O computador virtual está iniciando. A janela do DexTor OS será aberta em breve.", "Aguardando o DexTor OS", "O DexTor OS está carregando na nova janela. A tela pode ficar escura por alguns instantes.",
                "Mantenha a unidade USB conectada enquanto o DexTor OS estiver em execução.", "O DexTor OS está em execução", "O DexTor OS está aberto em uma janela maximizada. Encerre a sessão aqui antes de remover a unidade USB.",
                "Parar o DexTor OS", "Encerrando o DexTor OS com segurança", "O DexTor OS foi encerrado", "A sessão foi encerrada. Agora a unidade USB pode ser removida com segurança.",
                "A unidade USB DexTor foi desconectada", "O DexTor OS foi encerrado imediatamente. Talvez seja necessário verificar o armazenamento antes da próxima inicialização."),
            ["pl"] = new(
                "Sprawdzanie dysku USB DexTor", "Sprawdzamy, czy dysk USB i prywatna pamięć są nadal podłączone.",
                "Przygotowywanie składników uruchamiania", "Sprawdzamy pliki potrzebne do otwarcia DexTor OS na tym komputerze.",
                "Podłączanie prywatnej pamięci", "Prywatna pamięć jest otwierana bezpośrednio z dysku USB.",
                "Uruchamianie DexTor OS", "Prosimy o cierpliwość. Uruchomienie komputera wirtualnego może potrwać kilka minut. Poczekaj, aż DexTor OS załaduje się w nowym oknie, i nie odłączaj dysku USB.",
                "Komputer wirtualny jest uruchamiany. Za chwilę otworzy się okno DexTor OS.", "Oczekiwanie na DexTor OS", "DexTor OS ładuje się w nowym oknie. Ekran może przez chwilę pozostać ciemny.",
                "Nie odłączaj dysku USB podczas działania DexTor OS.", "DexTor OS jest uruchomiony", "DexTor OS jest otwarty w zmaksymalizowanym oknie. Zakończ sesję tutaj przed odłączeniem dysku USB.",
                "Zatrzymaj DexTor OS", "Bezpieczne zatrzymywanie DexTor OS", "DexTor OS został zatrzymany", "Sesja została zamknięta. Można teraz bezpiecznie odłączyć dysk USB.",
                "Dysk USB DexTor został odłączony", "DexTor OS został natychmiast zatrzymany. Przed następnym uruchomieniem może być potrzebne sprawdzenie pamięci."),
            ["tr"] = new(
                "DexTor USB sürücüsü kontrol ediliyor", "USB sürücüsünün ve özel depolama alanının bağlı olduğunu kontrol ediyoruz.",
                "Başlatma bileşenleri hazırlanıyor", "DexTor OS'yi bu bilgisayarda açmak için gereken dosyalar kontrol ediliyor.",
                "Özel depolama alanınız bağlanıyor", "Özel depolama alanı doğrudan USB sürücüsünden açılıyor.",
                "DexTor OS başlatılıyor", "Lütfen sabırlı olun. Sanal bilgisayarın başlaması birkaç dakika sürebilir. DexTor OS'nin yeni pencerede tamamen yüklenmesini bekleyin ve USB sürücüsünü bağlı tutun.",
                "Sanal bilgisayar başlatılıyor. DexTor OS penceresi birazdan açılacak.", "DexTor OS bekleniyor", "DexTor OS yeni pencerede yükleniyor. Ekran kısa bir süre karanlık kalabilir.",
                "DexTor OS çalışırken USB sürücüsünü bağlı tutun.", "DexTor OS çalışıyor", "DexTor OS büyütülmüş bir pencerede açık. USB sürücüsünü çıkarmadan önce oturumu burada durdurun.",
                "DexTor OS'yi durdur", "DexTor OS güvenli şekilde durduruluyor", "DexTor OS durduruldu", "Oturum kapatıldı. USB sürücüsünü artık güvenle çıkarabilirsiniz.",
                "DexTor USB bağlantısı kesildi", "DexTor OS hemen durduruldu. Bir sonraki başlatmadan önce depolama alanının kontrol edilmesi gerekebilir."),
            ["zh-CN"] = new(
                "正在检查 DexTor USB", "正在确认 USB 驱动器和私人存储仍然连接。",
                "正在准备启动组件", "正在检查在此电脑上打开 DexTor OS 所需的文件。",
                "正在连接私人存储", "正在直接从 USB 驱动器打开私人存储。",
                "正在启动 DexTor OS", "请耐心等待。启动虚拟电脑可能需要几分钟。请等待 DexTor OS 在新窗口中完全加载，并保持 USB 驱动器连接。",
                "虚拟电脑正在启动。DexTor OS 窗口即将打开。", "正在等待 DexTor OS", "DexTor OS 正在新窗口中加载。屏幕可能会短暂保持黑色。",
                "DexTor OS 运行时，请保持 USB 驱动器连接。", "DexTor OS 正在运行", "DexTor OS 已在最大化窗口中打开。移除 USB 驱动器前，请先在此停止会话。",
                "停止 DexTor OS", "正在安全停止 DexTor OS", "DexTor OS 已停止", "会话已关闭。现在可以安全移除 USB 驱动器。",
                "DexTor USB 已断开", "DexTor OS 已立即停止。下次启动前可能需要检查存储。")
        };

    private static readonly Dictionary<string, (string Description, string Confirmation)> UsbSafetyTexts =
        new Dictionary<string, (string, string)>
        {
            ["de"] = ("Schließen Sie ein USB-Laufwerk mit mindestens 8 GB an. Es wird erst nach Ihrer Bestätigung gelöscht.", "Dieses USB-Laufwerk vollständig löschen?\n\nLaufwerk: {drive}\nPhysischer Datenträger: {disk}\nModell: {model}\nKapazität: {capacity}\n\nAlle Dateien und Partitionen werden dauerhaft gelöscht. Dies kann nicht rückgängig gemacht werden."),
            ["es"] = ("Conecta una unidad USB de al menos 8 GB. Solo se borrará después de tu confirmación.", "¿Borrar por completo esta unidad USB?\n\nUnidad: {drive}\nDisco físico: {disk}\nModelo: {model}\nCapacidad: {capacity}\n\nTodos los archivos y particiones se eliminarán permanentemente. Esta acción no se puede deshacer."),
            ["fr"] = ("Connectez une clé USB d’au moins 8 Go. Elle ne sera effacée qu’après votre confirmation.", "Effacer entièrement cette clé USB ?\n\nLecteur : {drive}\nDisque physique : {disk}\nModèle : {model}\nCapacité : {capacity}\n\nTous les fichiers et partitions seront définitivement supprimés. Cette action est irréversible."),
            ["pt-BR"] = ("Conecte uma unidade USB de pelo menos 8 GB. Ela só será apagada após sua confirmação.", "Apagar completamente esta unidade USB?\n\nUnidade: {drive}\nDisco físico: {disk}\nModelo: {model}\nCapacidade: {capacity}\n\nTodos os arquivos e partições serão removidos permanentemente. Esta ação não pode ser desfeita."),
            ["pl"] = ("Podłącz dysk USB o pojemności co najmniej 8 GB. Zostanie wymazany dopiero po potwierdzeniu.", "Całkowicie wymazać ten dysk USB?\n\nDysk: {drive}\nDysk fizyczny: {disk}\nModel: {model}\nPojemność: {capacity}\n\nWszystkie pliki i partycje zostaną trwale usunięte. Tej operacji nie można cofnąć."),
            ["tr"] = ("En az 8 GB kapasiteli bir USB sürücüsü bağlayın. Yalnızca onayınızdan sonra silinir.", "Bu USB sürücüsü tamamen silinsin mi?\n\nSürücü: {drive}\nFiziksel disk: {disk}\nModel: {model}\nKapasite: {capacity}\n\nTüm dosyalar ve bölümler kalıcı olarak silinecek. Bu işlem geri alınamaz."),
            ["zh-CN"] = ("请连接容量至少为 8 GB 的 USB 驱动器。只有在您确认后才会擦除。", "要完全擦除此 USB 驱动器吗？\n\n驱动器：{drive}\n物理磁盘：{disk}\n型号：{model}\n容量：{capacity}\n\n此 USB 上的所有文件和分区都将被永久删除，且无法撤销。")
        };

    private static readonly IReadOnlyDictionary<string, string> German = new Dictionary<string, string>
    {
        ["launcher.wizard.window_title"] = "DexTor OS einrichten",
        ["launcher.wizard.brand_promise"] = "Ihr tragbarer privater Arbeitsbereich",
        ["launcher.wizard.back"] = "Zurück",
        ["launcher.close"] = "Schließen",
        ["launcher.primary_action_continue"] = "Weiter",
        ["launcher.primary_action_refresh"] = "Erneut prüfen",
        ["launcher.primary_action_prepare"] = "Diesen PC vorbereiten",
        ["launcher.primary_action_restart_required"] = "Jetzt neu starten",
        ["launcher.primary_action_start_dextor"] = "DexTor starten",
        ["launcher.wizard.step.intro"] = "Willkommen",
        ["launcher.wizard.step.host"] = "Dieser PC",
        ["launcher.wizard.step.image"] = "Download",
        ["launcher.wizard.step.usb"] = "USB-Laufwerk",
        ["launcher.wizard.step.create"] = "Erstellen",
        ["launcher.wizard.step.complete"] = "Fertig",
        ["launcher.wizard.intro.title"] = "DexTor OS USB-Laufwerk erstellen",
        ["launcher.wizard.intro.description"] = "Dieser Assistent erstellt einen tragbaren DexTor OS Arbeitsbereich für diesen Computer.",
        ["launcher.wizard.intro.item_pc"] = "Prüfen, ob dieser PC bereit ist",
        ["launcher.wizard.intro.item_image"] = "Die neueste DexTor OS Version laden und prüfen",
        ["launcher.wizard.intro.item_usb"] = "Ein USB-Laufwerk auswählen und vorbereiten",
        ["launcher.wizard.intro.erase_notice"] = "Sie behalten die Kontrolle. Vor dem Löschen fragen wir immer nach.",
        ["launcher.wizard.host.title"] = "Diesen PC prüfen",
        ["launcher.wizard.host.description"] = "Die Prüfung erfolgt automatisch und ändert nichts an Ihrem Computer.",
        ["launcher.wizard.host.checking"] = "Voraussetzungen werden geprüft…",
        ["launcher.wizard.host.wait"] = "Dies dauert normalerweise nur einen Moment.",
        ["launcher.wizard.host.item_windows"] = "Windows und Prozessor",
        ["launcher.wizard.host.item_runtime"] = "App-Unterstützung",
        ["launcher.wizard.host.item_virtualization"] = "Virtualisierung",
        ["launcher.wizard.host.item_connection"] = "Internetverbindung",
        ["launcher.wizard.host.ready"] = "Alles ist bereit. Sie können fortfahren.",
        ["launcher.wizard.image.title"] = "Neueste DexTor OS Version vorbereiten",
        ["launcher.wizard.image.description"] = "Eine geprüfte Kopie wird im unten angezeigten temporären Ordner gespeichert.",
        ["launcher.wizard.image.progress"] = "{percent}% · {downloaded} von {total} · {speed} · Restzeit {eta}",
        ["launcher.wizard.image.cancel"] = "Download abbrechen",
        ["launcher.wizard.image.cancelling"] = "Wird abgebrochen…",
        ["launcher.wizard.image.cancelled"] = "Download abgebrochen",
        ["launcher.image_setup.download"] = "Neueste Version laden",
        ["launcher.image_setup.download_required"] = "Das neueste geprüfte Abbild ist noch nicht gespeichert",
        ["launcher.image_setup.downloading"] = "Abbild wird geladen",
        ["launcher.image_setup.ready"] = "Das Abbild ist bereit",
        ["launcher.image_setup.cache_path"] = "Temporärer Ordner: {path}",
        ["launcher.wizard.usb.title"] = "USB-Laufwerk auswählen",
        ["launcher.wizard.usb.description"] = "Schließen Sie das USB-Laufwerk an, das Sie verwenden möchten.",
        ["launcher.wizard.usb.none"] = "Noch kein geeignetes USB-Laufwerk gefunden",
        ["launcher.wizard.usb.none_details"] = "Schließen Sie ein beschreibbares Laufwerk an und prüfen Sie erneut.",
        ["launcher.wizard.usb.check_again"] = "Erneut prüfen",
        ["launcher.wizard.usb.erase_title"] = "Das ausgewählte Laufwerk wird gelöscht",
        ["launcher.wizard.usb.erase_description"] = "Sichern Sie wichtige Dateien. Der gesamte Inhalt wird ersetzt.",
        ["launcher.wizard.usb.erase_action"] = "Löschen und erstellen",
        ["launcher.wizard.create.title"] = "DexTor OS Laufwerk wird erstellt",
        ["launcher.wizard.create.description"] = "Das ausgewählte USB-Laufwerk wird vorbereitet.",
        ["launcher.wizard.create.keep_connected"] = "Lassen Sie das USB-Laufwerk bis zum Abschluss angeschlossen.",
        ["launcher.wizard.complete.title"] = "Ihr DexTor OS USB-Laufwerk ist bereit",
        ["launcher.wizard.complete.description"] = "Das Laufwerk wurde geprüft und kann verwendet werden.",
        ["launcher.wizard.complete.finish"] = "Fertigstellen",
        ["launcher.wizard.complete.start_over"] = "Neu beginnen"
    };

    private static readonly IReadOnlyDictionary<string, string> Spanish = new Dictionary<string, string>
    {
        ["launcher.wizard.window_title"] = "Configurar DexTor OS", ["launcher.wizard.brand_promise"] = "Tu espacio de trabajo privado y portátil",
        ["launcher.wizard.back"] = "Atrás", ["launcher.close"] = "Cerrar", ["launcher.primary_action_continue"] = "Continuar", ["launcher.primary_action_refresh"] = "Comprobar de nuevo", ["launcher.primary_action_prepare"] = "Preparar este PC", ["launcher.primary_action_restart_required"] = "Reiniciar ahora", ["launcher.primary_action_start_dextor"] = "Iniciar DexTor",
        ["launcher.wizard.step.intro"] = "Bienvenida", ["launcher.wizard.step.host"] = "Este PC", ["launcher.wizard.step.image"] = "Descarga", ["launcher.wizard.step.usb"] = "Unidad USB", ["launcher.wizard.step.create"] = "Crear", ["launcher.wizard.step.complete"] = "Listo",
        ["launcher.wizard.intro.title"] = "Crea tu unidad USB de DexTor OS", ["launcher.wizard.intro.description"] = "Este asistente preparará un espacio de trabajo portátil de DexTor OS para este equipo.", ["launcher.wizard.intro.item_pc"] = "Comprobar que este PC esté preparado", ["launcher.wizard.intro.item_image"] = "Descargar y verificar la última versión", ["launcher.wizard.intro.item_usb"] = "Elegir y preparar una unidad USB", ["launcher.wizard.intro.erase_notice"] = "Tú tienes el control. Siempre pediremos permiso antes de borrar datos.",
        ["launcher.wizard.host.title"] = "Comprobemos este PC", ["launcher.wizard.host.description"] = "La comprobación es automática y no modifica el equipo.", ["launcher.wizard.host.checking"] = "Comprobando lo necesario…", ["launcher.wizard.host.wait"] = "Normalmente solo tarda un momento.", ["launcher.wizard.host.item_windows"] = "Windows y procesador", ["launcher.wizard.host.item_runtime"] = "Compatibilidad de la aplicación", ["launcher.wizard.host.item_virtualization"] = "Virtualización", ["launcher.wizard.host.item_connection"] = "Conexión a Internet", ["launcher.wizard.host.ready"] = "Todo está listo. Puedes continuar.",
        ["launcher.wizard.image.title"] = "Preparar la última versión de DexTor OS", ["launcher.wizard.image.description"] = "Guardaremos una copia verificada en la carpeta temporal indicada abajo.", ["launcher.wizard.image.progress"] = "{percent}% · {downloaded} de {total} · {speed} · quedan {eta}", ["launcher.wizard.image.cancel"] = "Cancelar descarga", ["launcher.wizard.image.cancelling"] = "Cancelando…", ["launcher.wizard.image.cancelled"] = "Descarga cancelada", ["launcher.image_setup.download"] = "Descargar última versión", ["launcher.image_setup.download_required"] = "La imagen verificada aún no está guardada", ["launcher.image_setup.downloading"] = "Descargando imagen", ["launcher.image_setup.ready"] = "La imagen está lista", ["launcher.image_setup.cache_path"] = "Carpeta temporal: {path}",
        ["launcher.wizard.usb.title"] = "Elige una unidad USB", ["launcher.wizard.usb.description"] = "Conecta la unidad USB que quieras utilizar.", ["launcher.wizard.usb.none"] = "Aún no se encontró una unidad adecuada", ["launcher.wizard.usb.none_details"] = "Conecta una unidad con escritura y vuelve a comprobar.", ["launcher.wizard.usb.check_again"] = "Comprobar de nuevo", ["launcher.wizard.usb.erase_title"] = "Se borrará la unidad seleccionada", ["launcher.wizard.usb.erase_description"] = "Guarda los archivos importantes. Se reemplazará todo el contenido.", ["launcher.wizard.usb.erase_action"] = "Borrar y crear",
        ["launcher.wizard.create.title"] = "Creando tu unidad DexTor OS", ["launcher.wizard.create.description"] = "Estamos preparando la unidad USB seleccionada.", ["launcher.wizard.create.keep_connected"] = "Mantén la unidad conectada hasta terminar.", ["launcher.wizard.complete.title"] = "Tu unidad USB de DexTor OS está lista", ["launcher.wizard.complete.description"] = "La unidad se verificó y está lista para usarse.", ["launcher.wizard.complete.finish"] = "Finalizar", ["launcher.wizard.complete.start_over"] = "Empezar de nuevo"
    };

    private static readonly IReadOnlyDictionary<string, string> French = new Dictionary<string, string>
    {
        ["launcher.wizard.window_title"] = "Configurer DexTor OS", ["launcher.wizard.brand_promise"] = "Votre espace de travail privé et portable",
        ["launcher.wizard.back"] = "Retour", ["launcher.close"] = "Fermer", ["launcher.primary_action_continue"] = "Continuer", ["launcher.primary_action_refresh"] = "Vérifier à nouveau", ["launcher.primary_action_prepare"] = "Préparer ce PC", ["launcher.primary_action_restart_required"] = "Redémarrer", ["launcher.primary_action_start_dextor"] = "Démarrer DexTor",
        ["launcher.wizard.step.intro"] = "Bienvenue", ["launcher.wizard.step.host"] = "Ce PC", ["launcher.wizard.step.image"] = "Téléchargement", ["launcher.wizard.step.usb"] = "Clé USB", ["launcher.wizard.step.create"] = "Création", ["launcher.wizard.step.complete"] = "Prêt",
        ["launcher.wizard.intro.title"] = "Créez votre clé USB DexTor OS", ["launcher.wizard.intro.description"] = "Cet assistant prépare un espace de travail DexTor OS portable pour cet ordinateur.", ["launcher.wizard.intro.item_pc"] = "Vérifier que ce PC est prêt", ["launcher.wizard.intro.item_image"] = "Télécharger et vérifier la dernière version", ["launcher.wizard.intro.item_usb"] = "Choisir et préparer une clé USB", ["launcher.wizard.intro.erase_notice"] = "Vous gardez le contrôle. Votre accord sera demandé avant tout effacement.",
        ["launcher.wizard.host.title"] = "Vérifions ce PC", ["launcher.wizard.host.description"] = "La vérification est automatique et ne modifie pas l’ordinateur.", ["launcher.wizard.host.checking"] = "Vérification en cours…", ["launcher.wizard.host.wait"] = "Cela ne prend généralement qu’un instant.", ["launcher.wizard.host.item_windows"] = "Windows et processeur", ["launcher.wizard.host.item_runtime"] = "Prise en charge de l’application", ["launcher.wizard.host.item_virtualization"] = "Virtualisation", ["launcher.wizard.host.item_connection"] = "Connexion Internet", ["launcher.wizard.host.ready"] = "Tout est prêt. Vous pouvez continuer.",
        ["launcher.wizard.image.title"] = "Préparer la dernière version de DexTor OS", ["launcher.wizard.image.description"] = "Une copie vérifiée sera conservée dans le dossier temporaire ci-dessous.", ["launcher.wizard.image.progress"] = "{percent}% · {downloaded} sur {total} · {speed} · reste {eta}", ["launcher.wizard.image.cancel"] = "Annuler le téléchargement", ["launcher.wizard.image.cancelling"] = "Annulation…", ["launcher.wizard.image.cancelled"] = "Téléchargement annulé", ["launcher.image_setup.download"] = "Télécharger la dernière version", ["launcher.image_setup.download_required"] = "L’image vérifiée n’est pas encore enregistrée", ["launcher.image_setup.downloading"] = "Téléchargement de l’image", ["launcher.image_setup.ready"] = "L’image est prête", ["launcher.image_setup.cache_path"] = "Dossier temporaire : {path}",
        ["launcher.wizard.usb.title"] = "Choisissez une clé USB", ["launcher.wizard.usb.description"] = "Connectez la clé USB que vous souhaitez utiliser.", ["launcher.wizard.usb.none"] = "Aucune clé USB adaptée détectée", ["launcher.wizard.usb.none_details"] = "Connectez une clé inscriptible puis vérifiez à nouveau.", ["launcher.wizard.usb.check_again"] = "Vérifier à nouveau", ["launcher.wizard.usb.erase_title"] = "La clé sélectionnée sera effacée", ["launcher.wizard.usb.erase_description"] = "Sauvegardez vos fichiers importants. Tout le contenu sera remplacé.", ["launcher.wizard.usb.erase_action"] = "Effacer et créer",
        ["launcher.wizard.create.title"] = "Création de votre clé DexTor OS", ["launcher.wizard.create.description"] = "Nous préparons la clé USB sélectionnée.", ["launcher.wizard.create.keep_connected"] = "Laissez la clé connectée jusqu’à la fin.", ["launcher.wizard.complete.title"] = "Votre clé USB DexTor OS est prête", ["launcher.wizard.complete.description"] = "La clé a été vérifiée et peut être utilisée.", ["launcher.wizard.complete.finish"] = "Terminer", ["launcher.wizard.complete.start_over"] = "Recommencer"
    };

    private static readonly IReadOnlyDictionary<string, string> PortugueseBrazil = CreateCompact(
        "Configurar DexTor OS", "Seu espaço de trabalho privado e portátil", "Voltar", "Fechar", "Continuar", "Bem-vindo", "Este PC", "Download", "Unidade USB", "Criar", "Pronto",
        "Crie sua unidade USB com DexTor OS", "Este assistente prepara um espaço de trabalho portátil do DexTor OS para este computador.", "Verificar se este PC está pronto", "Baixar e verificar a versão mais recente", "Escolher e preparar uma unidade USB", "Sempre pediremos permissão antes de apagar dados.",
        "Vamos verificar este PC", "A verificação é automática e não altera o computador.", "Preparar a versão mais recente do DexTor OS", "Uma cópia verificada será mantida na pasta temporária mostrada abaixo.", "{percent}% · {downloaded} de {total} · {speed} · restam {eta}", "Cancelar download", "Download cancelado",
        "Escolha uma unidade USB", "Conecte a unidade USB que deseja usar.", "Nenhuma unidade adequada encontrada", "Verificar novamente", "Apagar e criar", "Criando sua unidade DexTor OS", "Estamos preparando a unidade USB selecionada.", "Mantenha a unidade conectada até terminar.", "Sua unidade USB DexTor OS está pronta", "A unidade foi verificada e está pronta para uso.", "Concluir", "Começar novamente");

    private static readonly IReadOnlyDictionary<string, string> Polish = CreateCompact(
        "Skonfiguruj DexTor OS", "Twoja przenośna prywatna przestrzeń robocza", "Wstecz", "Zamknij", "Kontynuuj", "Witamy", "Ten komputer", "Pobieranie", "Dysk USB", "Tworzenie", "Gotowe",
        "Utwórz dysk USB z DexTor OS", "Ten kreator przygotuje przenośne środowisko DexTor OS dla tego komputera.", "Sprawdzić gotowość komputera", "Pobrać i zweryfikować najnowszą wersję", "Wybrać i przygotować dysk USB", "Przed usunięciem danych zawsze poprosimy o zgodę.",
        "Sprawdźmy ten komputer", "Sprawdzanie jest automatyczne i nie zmienia ustawień komputera.", "Przygotuj najnowszą wersję DexTor OS", "Zweryfikowana kopia zostanie zapisana w folderze tymczasowym poniżej.", "{percent}% · {downloaded} z {total} · {speed} · pozostało {eta}", "Anuluj pobieranie", "Pobieranie anulowane",
        "Wybierz dysk USB", "Podłącz dysk USB, którego chcesz użyć.", "Nie znaleziono odpowiedniego dysku", "Sprawdź ponownie", "Wyczyść i utwórz", "Tworzenie dysku DexTor OS", "Przygotowujemy wybrany dysk USB.", "Nie odłączaj dysku do zakończenia.", "Dysk USB z DexTor OS jest gotowy", "Dysk został sprawdzony i jest gotowy do użycia.", "Zakończ", "Zacznij od nowa");

    private static readonly IReadOnlyDictionary<string, string> Turkish = CreateCompact(
        "DexTor OS'yi kur", "Taşınabilir özel çalışma alanınız", "Geri", "Kapat", "Devam", "Hoş geldiniz", "Bu bilgisayar", "İndirme", "USB sürücüsü", "Oluştur", "Hazır",
        "DexTor OS USB sürücünüzü oluşturun", "Bu sihirbaz bu bilgisayar için taşınabilir bir DexTor OS çalışma alanı hazırlayacak.", "Bu bilgisayarın hazır olduğunu kontrol et", "En son sürümü indir ve doğrula", "Bir USB sürücüsü seç ve hazırla", "Verileri silmeden önce her zaman izin isteyeceğiz.",
        "Bu bilgisayarı kontrol edelim", "Kontrol otomatiktir ve bilgisayarınızda değişiklik yapmaz.", "En yeni DexTor OS sürümünü hazırla", "Doğrulanmış bir kopya aşağıdaki geçici klasörde tutulacak.", "{percent}% · {downloaded} / {total} · {speed} · kalan {eta}", "İndirmeyi iptal et", "İndirme iptal edildi",
        "Bir USB sürücüsü seçin", "Kullanmak istediğiniz USB sürücüsünü bağlayın.", "Uygun USB sürücüsü bulunamadı", "Tekrar kontrol et", "Sil ve oluştur", "DexTor OS sürücünüz oluşturuluyor", "Seçilen USB sürücüsü hazırlanıyor.", "Tamamlanana kadar sürücüyü bağlı tutun.", "DexTor OS USB sürücünüz hazır", "Sürücü doğrulandı ve kullanıma hazır.", "Bitir", "Baştan başla");

    private static readonly IReadOnlyDictionary<string, string> ChineseSimplified = CreateCompact(
        "设置 DexTor OS", "您的便携式私人工作空间", "返回", "关闭", "继续", "欢迎", "此电脑", "下载", "USB 驱动器", "创建", "就绪",
        "创建 DexTor OS USB 驱动器", "此向导将为这台电脑准备便携式 DexTor OS 工作空间。", "检查此电脑是否已准备好", "下载并验证最新版本", "选择并准备 USB 驱动器", "删除数据前，我们一定会征得您的同意。",
        "检查这台电脑", "检查会自动完成，不会更改您的电脑。", "准备最新版 DexTor OS", "经过验证的副本将保存在下方显示的临时文件夹中。", "{percent}% · {downloaded}/{total} · {speed} · 剩余 {eta}", "取消下载", "下载已取消",
        "选择 USB 驱动器", "连接您要使用的 USB 驱动器。", "尚未找到合适的 USB 驱动器", "重新检查", "擦除并创建", "正在创建 DexTor OS 驱动器", "正在准备所选 USB 驱动器。", "完成前请勿断开 USB 驱动器。", "DexTor OS USB 驱动器已就绪", "驱动器已验证，可以使用。", "完成", "重新开始");

    private static Dictionary<string, string> CreateCompact(
        string windowTitle, string promise, string back, string close, string continueText,
        string introStep, string hostStep, string imageStep, string usbStep, string createStep, string completeStep,
        string introTitle, string introDescription, string introPc, string introImage, string introUsb, string introNotice,
        string hostTitle, string hostDescription, string imageTitle, string imageDescription, string imageProgress, string cancel, string cancelled,
        string usbTitle, string usbDescription, string usbNone, string usbCheck, string eraseAction,
        string createTitle, string createDescription, string keepConnected,
        string completeTitle, string completeDescription, string finish, string startOver)
    {
        return new Dictionary<string, string>
        {
            ["launcher.wizard.window_title"] = windowTitle, ["launcher.wizard.brand_promise"] = promise,
            ["launcher.wizard.back"] = back, ["launcher.close"] = close, ["launcher.primary_action_continue"] = continueText,
            ["launcher.wizard.step.intro"] = introStep, ["launcher.wizard.step.host"] = hostStep, ["launcher.wizard.step.image"] = imageStep,
            ["launcher.wizard.step.usb"] = usbStep, ["launcher.wizard.step.create"] = createStep, ["launcher.wizard.step.complete"] = completeStep,
            ["launcher.wizard.intro.title"] = introTitle, ["launcher.wizard.intro.description"] = introDescription,
            ["launcher.wizard.intro.item_pc"] = introPc, ["launcher.wizard.intro.item_image"] = introImage,
            ["launcher.wizard.intro.item_usb"] = introUsb, ["launcher.wizard.intro.erase_notice"] = introNotice,
            ["launcher.wizard.host.title"] = hostTitle, ["launcher.wizard.host.description"] = hostDescription,
            ["launcher.wizard.image.title"] = imageTitle, ["launcher.wizard.image.description"] = imageDescription,
            ["launcher.wizard.image.progress"] = imageProgress,
            ["launcher.wizard.image.cancel"] = cancel, ["launcher.wizard.image.cancelled"] = cancelled,
            ["launcher.wizard.usb.title"] = usbTitle, ["launcher.wizard.usb.description"] = usbDescription,
            ["launcher.wizard.usb.none"] = usbNone, ["launcher.wizard.usb.check_again"] = usbCheck, ["launcher.wizard.usb.erase_action"] = eraseAction,
            ["launcher.wizard.create.title"] = createTitle, ["launcher.wizard.create.description"] = createDescription,
            ["launcher.wizard.create.keep_connected"] = keepConnected,
            ["launcher.wizard.complete.title"] = completeTitle, ["launcher.wizard.complete.description"] = completeDescription,
            ["launcher.wizard.complete.finish"] = finish,
            ["launcher.wizard.complete.start_over"] = startOver
        };
    }
}
