namespace SeoLoodoi.Application.Billing;

public static class PlanCatalog
{
    public const string Free = "Free";
    public const string Starter = "Starter";
    public const string Pro = "Pro";
    public const string Enterprise = "Enterprise";

    private static readonly IReadOnlyList<PlanDefinitionDto> AllPlans =
    [
        new(
            PlanId: Free,
            Name: "رایگان (Free)",
            Description: "برای شروع، تست و بررسی اولیه وب‌سایت‌های کوچک و وبلاگ‌های شخصی",
            PriceTomansPerMonth: 0,
            MaxProjects: 1,
            MaxPagesPerMonth: 100,
            MaxKeywords: 10,
            MaxCompetitors: 1,
            MaxTeamMembers: 1,
            MaxAiCreditsPerMonth: 5,
            RetentionDays: 7,
            Features: ["خزش دستی صفحات", "گزارش‌های JSON", "بررسی اساسی ۲۰ فاکتور سئو"]
        ),
        new(
            PlanId: Starter,
            Name: "استارتر (Starter)",
            Description: "برای استارتاپ‌ها، سایت‌های شرکتی و وبلاگ‌های رو به رشد",
            PriceTomansPerMonth: 390_000,
            MaxProjects: 3,
            MaxPagesPerMonth: 1_000,
            MaxKeywords: 25,
            MaxCompetitors: 3,
            MaxTeamMembers: 3,
            MaxAiCreditsPerMonth: 25,
            RetentionDays: 30,
            Features: ["خزش زمان‌بندی‌شده هفتگی", "گزارش PDF با فونت فارسی وزیرمتن", "پایش ۳ رقیب تجاری", "سیستم هشدارهای داشبورد و ایمیل", "تحلیل کلمات کلیدی و فرصت‌ها"]
        ),
        new(
            PlanId: Pro,
            Name: "حرفه‌ای (Pro)",
            Description: "برای فروشگاه‌های اینترنتی، آژانس‌های دیجیتال مارکتینگ و پروژه‌های جدی",
            PriceTomansPerMonth: 1_290_000,
            MaxProjects: 10,
            MaxPagesPerMonth: 10_000,
            MaxKeywords: 150,
            MaxCompetitors: 10,
            MaxTeamMembers: 10,
            MaxAiCreditsPerMonth: 100,
            RetentionDays: 90,
            Features: ["خزش روزانه خودکار", "همگام‌سازی کامل گوگل سرچ کنسول (GSC)", "وب‌هوک‌های لحظه‌ای با امضای HMAC", "تحلیل شباهت عمیق محتوا", "پایش ۱۰ رقیب همزمان", "همکاری تیمی تا ۱۰ عضو"]
        ),
        new(
            PlanId: Enterprise,
            Name: "سازمانی (Enterprise)",
            Description: "برای برندهای بزرگ، رسانه‌ها، فروشگاه‌های زنجیره‌ای و آژانس‌های بین‌المللی",
            PriceTomansPerMonth: 4_500_000,
            MaxProjects: 50,
            MaxPagesPerMonth: 100_000,
            MaxKeywords: 1_000,
            MaxCompetitors: 50,
            MaxTeamMembers: 50,
            MaxAiCreditsPerMonth: 500,
            RetentionDays: 365,
            Features: ["خزش پرسرعت با اولویت ویژه (Priority Queue)", "حفظ تاریخچه ۳۶۵ روزه ممیزی‌ها", "گزارش‌های White-label سازمانی", "دسترسی اختصاصی API", "پشتیبانی اختصاصی VIP"]
        )
    ];

    public static IReadOnlyList<PlanDefinitionDto> GetPlans() => AllPlans;

    public static PlanDefinitionDto GetPlan(string? planId)
    {
        if (string.IsNullOrWhiteSpace(planId)) return AllPlans[1]; // Default Starter
        return AllPlans.FirstOrDefault(p => string.Equals(p.PlanId, planId.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? AllPlans[1];
    }

    public static bool IsValidPlan(string? planId) =>
        !string.IsNullOrWhiteSpace(planId) && AllPlans.Any(p => string.Equals(p.PlanId, planId.Trim(), StringComparison.OrdinalIgnoreCase));
}
