# راهنمای اجرای SEO Loodoi

## پیش‌نیازها

- .NET SDK 10
- Node.js 20 یا جدیدتر
- npm 10 یا جدیدتر
- برای اجرای Production-like: PostgreSQL 16 یا جدیدتر

بررسی نسخه‌ها:

```bash
dotnet --version
node --version
npm --version
```

---

## روش اول: اجرای سریع برای تست محلی

در این روش API از دیتابیس In-Memory استفاده می‌کند. PostgreSQL لازم نیست، اما با توقف API داده‌ها پاک می‌شوند.

### 1. اجرای Backend

در ترمینال اول، از پوشه اصلی پروژه:

### Windows PowerShell

```powershell
$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet restore SeoLoodoi.slnx
dotnet run --project src/SeoLoodoi.Api --urls http://localhost:5080
```

### Linux / macOS

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet restore SeoLoodoi.slnx
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/SeoLoodoi.Api --urls http://localhost:5080
```

تست سلامت API:

```text
http://localhost:5080/health
```

### 2. اجرای Frontend

در ترمینال دوم:

```bash
cd src/SeoLoodoi.Web
npm install
npm run dev
```

در PowerShell اگر اجرای `npm.ps1` مسدود بود از `npm.cmd` استفاده کنید. اگر خطای `Cannot find native binding` مربوط به Rolldown دریافت شد:

```powershell
Remove-Item -Recurse -Force .\node_modules
Remove-Item -Force .\package-lock.json
npm.cmd cache verify
npm.cmd install --include=optional
npm.cmd run dev
```

اگر Binding ویندوز همچنان نصب نشد:

```powershell
npm.cmd install --save-dev @rolldown/binding-win32-x64-msvc
npm.cmd run dev
```

سپس باز کنید:

```text
http://localhost:5173
```

در رابط کاربری:

1. حساب بسازید.
2. وارد شوید.
3. یک پروژه با URL عمومی HTTP/HTTPS ایجاد کنید.
4. روی «شروع خزش» بزنید.
5. پیشرفت، Issues و Score را مشاهده کنید.

> آدرس‌های localhost، شبکه خصوصی، link-local و metadata توسط محافظ SSRF عمداً قابل Crawl نیستند.

---

## روش دوم: اجرا با PostgreSQL

### 1. ساخت دیتابیس

نمونه:

```sql
CREATE USER seo_loodoi WITH PASSWORD 'replace-with-a-strong-password';
CREATE DATABASE seo_loodoi OWNER seo_loodoi;
```

### 2. تنظیم Connection String

### Windows PowerShell

```powershell
$env:ASPNETCORE_ENVIRONMENT="Development"
$env:DatabaseProvider="Postgres"
$env:ConnectionStrings__Postgres="Host=localhost;Port=5432;Database=seo_loodoi;Username=seo_loodoi;Password=YOUR_PASSWORD"
```

### Linux / macOS

```bash
export ASPNETCORE_ENVIRONMENT=Development
export DatabaseProvider=Postgres
export ConnectionStrings__Postgres='Host=localhost;Port=5432;Database=seo_loodoi;Username=seo_loodoi;Password=YOUR_PASSWORD'
```

رمز واقعی را داخل `appsettings.json` یا Git قرار ندهید.

### 3. اجرای Migration

اگر ابزار EF نصب نیست:

```bash
dotnet tool install --global dotnet-ef --version 10.0.4
```

سپس:

```bash
dotnet ef database update \
  --project src/SeoLoodoi.Infrastructure \
  --startup-project src/SeoLoodoi.Api
# آخرین migration شامل capabilityهای محصول، اعضا، متریک‌ها، evidence تکمیلی، audit log و outbox هشدار است.
# فایل Designer و ModelSnapshot این migration نیز با مدل فعلی هم‌تراز شده‌اند.
# برای لینک تأیید ایمیل، Application__PublicBaseUrl باید آدرس عمومی API و Application__WebBaseUrl آدرس عمومی UI باشد.
```

در PowerShell می‌توانید دستور را در یک خط اجرا کنید.

### 4. اجرای سرویس‌ها

```bash
dotnet run --project src/SeoLoodoi.Api --urls http://localhost:5080
```

در ترمینال دیگر:

```bash
cd src/SeoLoodoi.Web
npm install
npm run dev
```

---

## اجرای تست‌ها

از پوشه اصلی:

```bash
dotnet restore SeoLoodoi.slnx
dotnet build SeoLoodoi.slnx --no-restore
dotnet test SeoLoodoi.slnx --no-build
```

Frontend:

```bash
cd src/SeoLoodoi.Web
npm install
npm run build
npm audit --audit-level=high
```

بررسی پکیج‌های NuGet:

```bash
dotnet list SeoLoodoi.slnx package --vulnerable --include-transitive
```

نتیجه‌ای که باید در CI مرجع اجرا و ثبت شود:

- Backend: `dotnet build` بدون Error و Warning
- Unit/Security tests و PostgreSQL integration tests
- Frontend production build و lint
- npm/NuGet known vulnerabilities
- End-to-end flow: Register → Project → Crawl → Analyze → Score

در محیط کاری فعلی اگر .NET 10 SDK نصب نباشد، فقط بررسی‌های Frontend و `git diff --check` قابل اجرای محلی هستند.

---

## تنظیم آدرس API برای Frontend

در Development، Vite مسیر `/api` را به `http://127.0.0.1:5080` Proxy می‌کند.

برای آدرس متفاوت:

```bash
VITE_API_URL=https://api.example.com npm run build
```

در Production بهتر است UI و API پشت یک Reverse Proxy و روی یک Origin ارائه شوند.

## نکات مهم

- حالت In-Memory فقط برای Preview و تست سریع است.
- Production باید از PostgreSQL، HTTPS، Secret Store و Reverse Proxy استفاده کند.
- قبل از انتشار عمومی، مراحل باقی‌مانده در `docs/IMPLEMENTATION-STATUS.md` را بررسی کنید.
- محصول هنوز پایان تمام فازهای Release نیست؛ وضعیت واقعی قابلیت‌ها در فایل مذکور ثبت شده است.
