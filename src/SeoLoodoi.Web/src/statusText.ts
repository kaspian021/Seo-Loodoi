export const faAnalysisStatus = (value?: string | null) =>
  (
    {
      Pending: 'تحلیل در انتظار',
      Running: 'تحلیل در حال اجرا',
      Succeeded: 'تحلیل موفق',
      Failed: 'تحلیل ناموفق',
      NoData: 'بدون داده برای تحلیل',
    }[value ?? ''] ?? value ?? '—'
  )

export const analysisHint = (value?: string | null) =>
  (
    {
      Pending: 'خزش تکمیل شده و تحلیل به‌زودی اجرا می‌شود. این صفحه خودکار به‌روز می‌شود.',
      Running: 'تحلیل شواهد خزش در حال اجراست. امتیاز و مشکلات پس از اتمام نمایش داده می‌شوند.',
      Succeeded: 'تحلیل با موفقیت انجام شد و امتیازها از شواهد همین خزش محاسبه شده‌اند.',
      Failed: 'تحلیل این خزش ناموفق بود. با «تلاش مجدد تحلیل» می‌توانید دوباره اجرا کنید.',
      NoData: 'این خزش هیچ صفحه‌ای ذخیره نکرده است؛ امتیازی محاسبه نمی‌شود.',
    }[value ?? ''] ?? ''
  )

export const fmtScore = (value?: number | null) =>
  value === null || value === undefined ? '—' : value.toLocaleString('fa-IR', { maximumFractionDigits: 1 })

export const faCategory = (key: string) =>
  (
    {
      technical: 'فنی',
      indexability: 'قابلیت ایندکس',
      onPage: 'درون‌صفحه',
      content: 'محتوا',
      links: 'لینک‌ها',
      structuredData: 'داده ساخت‌یافته',
      performance: 'عملکرد',
      international: 'بین‌المللی',
      security: 'امنیت',
    }[key] ?? key
  )

export const isAnalysisActive = (value?: string | null) => value === 'Pending' || value === 'Running'

export const competitorCrawlHint = (crawl?: { status: string; pagesCrawled: number } | null) => {
  if (!crawl) return 'هنوز خزش نشده'
  const pages = crawl.pagesCrawled.toLocaleString('fa-IR')
  const base: Record<string, string> = {
    Queued: 'در صف خزش',
    Running: `در حال خزش (${pages} صفحه)`,
    Completed: `خزش کامل (${pages} صفحه واقعی)`,
    Failed: 'خزش ناموفق',
    Cancelled: 'خزش لغوشده',
  }
  return base[crawl.status] ?? crawl.status
}
