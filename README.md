# Advanced SystemCare 20 — Native C# (.NET 8.0 WPF) Implementation

تحويل كامل وشامل لتطبيق **Advanced SystemCare 20** إلى لغة **C#** كبرنامج سطح مكتب أصيل لنظام ويندوز مبني على إطار عمل **.NET 8.0 WPF** وفقاً للتقرير الهندسي لعام 2026.

---

## 🌟 مميزات النسخة المحولة إلى C#:

### 1. معمارية واجهة المستخدم (Modern Fluent Dark Slate UI)
* **نافذة مخصصة خالية من الإطار التقليدي (Frameless Window):**
  * دعم السحب الكامل للنافذة (`DragMove`).
  * لوحة ألوان مستوحاة من نمط ويندوز 11 المتطور (`#070A12`, `#0B1120`, `#0F172A`, `#06B6D4`).
  * شريط عنوان مخصص مع زر الترقية الذهبي **Upgrade to PRO**، وأيقونة فحص قاعدة البيانات، وأزرار التحكم (تصغير، تكبير، إغلاق).
* **القائمة الجانبية (Sidebar) ذات الستة أقسام:**
  1. **Care (العناية):** الفحص الشامل والإصلاح.
  2. **Custom Clean (التنظيف المخصص):** شارة `NEW` مع شجرة الملفات المكررة.
  3. **Speed Up (التسريع):** بطاقات التربو بوست وتحسين الإقلاع وتفريغ الرام.
  4. **Protect (الحماية):** دروع الحماية وصندوق الأدوات (Toolbox).
  5. **Software Updater (محدث البرامج):** فحص وتحديث البرامج بنقرة واحدة.
  6. **Action Center (مركز الإجراءات):** تقييم صحة النظام وسجل صيانة Realm DB.

---

### 2. تكامل واجهات ويندوز (Windows APIs)
* **تقليص ذاكرة البرنامج نفسه:** `SetProcessWorkingSetSize(-1, -1)` و`EmptyWorkingSet` على عملية SystemCare فقط. لا يُفرَّغ باقي العمليات لأن ذلك يبطئ سطح المكتب.
* **أولوية العملية (Turbo Boost):** `SetPriorityClass` يغيّر أولوية SystemCare نفسه بين `ABOVE_NORMAL_PRIORITY_CLASS` و`HIGH_PRIORITY_CLASS`. لا يغيّر أولوية الألعاب أو البرامج الأخرى، وهو متوقف افتراضياً.
* **رفض الروابط الرمزية:** الحذف والتمزيق يتخطيان أي مسار يحمل `FileAttributes.ReparsePoint`.
* **الكتابة فوق الملف قبل حذفه:** ثلاث تمريرات (أصفار، آحاد، بايتات عشوائية) مع `FileOptions.WriteThrough`. على أقراص SSD قد تبقى نسخة قديمة.
* **برامج بدء التشغيل:** مفاتيح `Run` بالإضافة إلى ملفات مجلد Startup (اختصارات المستخدم والمجلد المشترك)، والتعليل عبر `StartupApproved` كما يفعل مدير المهام.
* **قراءات النظام:** المعالج عبر `GetSystemTimes`، والذاكرة عبر `GlobalMemoryStatusEx`، ونسبة امتلاء قرص النظام، ومعدل الشبكة. الأداة العائمة تعرض امتلاء القرص لا حرارته.

---

### 3. الأداة العائمة لمراقبة الأداء (Performance Monitor Widget - الشكل 6)
* نافذة سطح مكتب عائمة مستقلة (`Topmost = True`) قابلة للسحب والإفلات في أي مكان على الشاشة.
* **الحالة المصغرة:** رسوم بيانية خطية حية (`Sparklines`) للمعالج والذاكرة تتجدد كل ثانية ونصف.
* **الحالة الموسعة عند تمرير الفأرة (Hover):**
  * نسبة امتلاء قرص النظام، وسرعات الرفع والتحميل لحظياً.
  * زر **Clean Memory**: يقلّص الذاكرة التي يستخدمها SystemCare نفسه فقط.

---

### 4. صينية النظام (System Tray - دور ASCTray.exe)
* أيقونة دائمة بجوار ساعة الويندوز في علبة النظام (`NotifyIcon`) تتيح:
  * فتح البرنامج بضغطة مزدوجة.
  * تنظيف سريع للرام من القائمة الفرعية (`Clean RAM Now`).
  * إظهار أو إخفاء أداة مراقبة الأداء العائمة.
  * الخروج الكامل من البرنامج.

---

### 5. التخزين المحلي
* الملف `Database/RealmLocalDatabase.cs` مخزن JSON محلي، وليس قاعدة Realm.
* يُحفظ في `%LocalAppData%\AdvancedSystemCare20\systemcare_realm_store.json` عبر كتابة ملف مؤقت ثم استبداله.
* البيانات الأولية (دروع، قائمة تحديثات، ملاحظات) أمثلة للواجهة. الفحص لا يولّد أعداد مشاكل عشوائية، ووحدات السجل والخصوصية والحماية والتحديث تظهر «غير متصل» ولا تدخل في الإصلاح.
* الإصلاح الحقيقي يحذف الملفات المؤقتة وذاكرة المتصفح الأقدم من 24 ساعة، ويمكنه تقليص ذاكرة SystemCare نفسه.

---

## 📁 هيكل مجلدات المشروع (C# .NET 8.0 WPF)

```text
C:\Users\Tamer Murad\.gemini\antigravity\scratch\systemcare-20-csharp\
│
├── Run.bat                      # تشغيل البرنامج عبر dotnet run
├── SystemCare20.csproj          # ملف إعدادات المشروع واستهداف net8.0-windows
├── App.xaml / App.xaml.cs       # نقطة بدء التطبيق وإدارة صينية النظام (ASCTray)
├── MainWindow.xaml / .cs        # الواجهة الرسومية الكاملة لنظام ASC 20
│
├── Models/
│   └── RealmModels.cs           # نماذج ومخططات Realm DB (ScanRecord, DuplicateGroup, etc.)
│
├── Database/
│   └── RealmLocalDatabase.cs    # مخزن JSON محلي (ليس Realm)
│
├── Services/
│   ├── WindowsNativeApi.cs      # دوال Win32 APIs (SetProcessWorkingSetSize, Symlink Safe Check)
│   ├── SystemMetrics.cs         # مستشعرات ومقاييس النظام الفعلية (CPU, RAM, Network)
│   ├── FileScanner.cs           # محرك فحص المكررات (MD5, Name+Size, Byte-by-byte)
│   └── StartupManager.cs        # مفاتيح Run وملفات مجلد Startup
│
└── Views/
    ├── PerformanceWidgetWindow.xaml     # نافذة الأداة العائمة لمراقبة الأداء
    └── PerformanceWidgetWindow.xaml.cs  # منطق الرسوم البيانية اللحظية وزر الصاروخ
```

---

## 🚀 كيفية البناء والتشغيل

### 1. البناء السريع (Build):
افتح موجه الأوامر (PowerShell) في مجلد المشروع ونفذ:
```powershell
cd "C:\Users\Tamer Murad\.gemini\antigravity\scratch\systemcare-20-csharp"
dotnet build
```

### 2. التشغيل المباشر (Run):
انقر نقراً مزدوجاً على `Run.bat` في مجلد المشروع، أو نفّذ:
```powershell
dotnet run -c Release
```
أو شغّل الملف التنفيذي بعد البناء:
📂 `bin\Release\net8.0-windows\SystemCare20.exe`

### 3. إنشاء ملف تنفيذي مستقل أحادي (Single-file Portable Exe):
```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```
ينتج ملفاً تنفيذياً واحداً مستقلاً بالكامل جاهزاً للعمل على أي جهاز ويندوز دون الحاجة لتثبيت أي متطلبات مسبقة.
