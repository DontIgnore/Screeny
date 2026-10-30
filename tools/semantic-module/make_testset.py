"""Synthetic retrieval test set: CC0 photos from Wikimedia Commons plus UI screenshots drawn with Pillow.

    work/venv/Scripts/python -I tools/semantic-module/make_testset.py

Writes work/testset/*.png|jpg and work/testset/testset.json (images, queries, relevant images per query).
No real screenshots are used. Photos are CC0 (checked via the Commons API, see PHOTOS); they stay in the git-ignored
work folder and are never committed.
"""
import json
import os
import random
import time
import urllib.request

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "work", "testset")
FONTS = r"C:\Windows\Fonts"

# name -> (Commons file page, 1280px thumbnail URL); all CC0 per Commons extmetadata (LicenseShortName = CC0).
PHOTOS = {
    "photo_cat_sitting.jpg": ("File:A photo of a cat sitting down.jpg",
                              "https://thumb.wikimedia.org/wikipedia/commons/thumb/7/7b/A_photo_of_a_cat_sitting_down.jpg/1280px-A_photo_of_a_cat_sitting_down.jpg"),
    "photo_cat_siamese.jpg": ("File:Siamese-Cat Andreas-LischkaPixabay.jpg",
                              "https://upload.wikimedia.org/wikipedia/commons/a/a1/Siamese-Cat_Andreas-LischkaPixabay.jpg"),
    "photo_dog_boxer.jpg": ("File:Photo of a female boxer dog in Iran Mostafa Meraji 04.jpg",
                            "https://thumb.wikimedia.org/wikipedia/commons/thumb/1/1f/Photo_of_a_female_boxer_dog_in_Iran_Mostafa_Meraji_04.jpg/1280px-Photo_of_a_female_boxer_dog_in_Iran_Mostafa_Meraji_04.jpg"),
    "photo_dog_my.jpg": ("File:My Dog (197495443).jpeg",
                         "https://thumb.wikimedia.org/wikipedia/commons/thumb/7/78/My_Dog_%28197495443%29.jpeg/1280px-My_Dog_%28197495443%29.jpeg"),
}

QUERIES = {
    "котик": ["photo_cat_sitting.jpg", "photo_cat_siamese.jpg", "chat_with_cat.png"],
    "cat": ["photo_cat_sitting.jpg", "photo_cat_siamese.jpg", "chat_with_cat.png"],
    "кошка": ["photo_cat_sitting.jpg", "photo_cat_siamese.jpg", "chat_with_cat.png"],
    "dog": ["photo_dog_boxer.jpg", "photo_dog_my.jpg"],
    "собака": ["photo_dog_boxer.jpg", "photo_dog_my.jpg"],
    "график": ["chart_bar.png", "chart_line_ru.png"],
    "chart": ["chart_bar.png", "chart_line_ru.png"],
    "error dialog": ["error_dialog.png"],
    "ошибка": ["error_dialog.png"],
    "счёт": ["invoice_ru.png", "invoice_en.png"],
    "invoice": ["invoice_ru.png", "invoice_en.png"],
    "code": ["code_editor.png"],
    "код": ["code_editor.png"],
    "map": ["map.png"],
    "карта": ["map.png"],
    "chat": ["chat.png", "chat_with_cat.png"],
    "переписка": ["chat.png", "chat_with_cat.png"],
    "login form": ["login_form.png"],
    "таблица": ["spreadsheet.png"],
    # Text-only concepts: nothing in the picture but the words says what it is (tests reading text in images,
    # including cross-language: the English query must find the Russian text and vice versa).
    "договор аренды": ["doc_lease_ru.png"],
    "rental agreement": ["doc_lease_ru.png"],
    "recipe": ["doc_recipe_ru.png"],
    "рецепт": ["doc_recipe_ru.png"],
    "flight booking": ["email_flight_en.png"],
    "билет на самолёт": ["email_flight_en.png"],
    "wifi password": ["note_wifi_en.png"],
    "пароль от wi-fi": ["note_wifi_en.png"],
    # Desktop density (1920x1080 / 2560x1440, 13-14 px UI text, concept only in the small text): what real screenshots
    # look like. Reported separately ("desk" subtotal).
    "hotel reservation": ["desk_mail_hotel.png"],
    "бронь отеля": ["desk_mail_hotel.png"],
    "tax notice": ["desk_browser_tax.png"],
    "налог": ["desk_browser_tax.png"],
    "vpn settings": ["desk_settings_vpn.png"],
    "настройки vpn": ["desk_settings_vpn.png"],
    "warranty card": ["desk_pdf_warranty.png"],
    "гарантийный талон": ["desk_pdf_warranty.png"],
}


def font(name, size):
    return ImageFont.truetype(os.path.join(FONTS, name), size)


def window(w, h, title, bg="#ffffff", dark=False):
    img = Image.new("RGB", (w, h), "#1e1e1e" if dark else bg)
    d = ImageDraw.Draw(img)
    d.rectangle([0, 0, w, 36], fill="#323233" if dark else "#f3f3f3")
    d.text((14, 8), title, font=font("segoeui.ttf", 16), fill="#cccccc" if dark else "#222222")
    for i, c in enumerate(["#e81123", "#888888", "#888888"]):
        d.text((w - 40 - i * 46, 6), "✕" if i == 0 else ("□" if i == 1 else "–"), font=font("segoeui.ttf", 16),
               fill="#cccccc" if dark else "#333333")
    return img, d


def code_editor():
    img, d = window(1600, 900, "Program.cs — Screeny — Visual Studio Code", dark=True)
    d.rectangle([0, 36, 52, 900], fill="#333333")
    d.rectangle([52, 36, 300, 900], fill="#252526")
    for i, f in enumerate(["Program.cs", "HistoryStore.cs", "Capture.cs", "Settings.cs", "App.xaml"]):
        d.text((70, 60 + i * 26), f, font=font("segoeui.ttf", 15), fill="#cccccc")
    lines = [
        ("using System;", "#569cd6"), ("using System.IO;", "#569cd6"), ("", None),
        ("namespace Screeny.History;", "#569cd6"), ("", None),
        ("public sealed class HistoryStore", "#4ec9b0"), ("{", "#d4d4d4"),
        ("    private readonly string _root;", "#9cdcfe"), ("", None),
        ("    public async Task<int> SaveAsync(Bitmap shot)", "#dcdcaa"), ("    {", "#d4d4d4"),
        ("        var path = Path.Combine(_root, $\"{DateTime.Now:yyyyMMdd-HHmmss}.png\");", "#ce9178"),
        ("        await using var fs = File.Create(path);", "#9cdcfe"),
        ("        shot.Save(fs, ImageFormat.Png);", "#d4d4d4"),
        ("        return await _index.AddAsync(path); // TODO: thumbnails", "#6a9955"),
        ("    }", "#d4d4d4"), ("}", "#d4d4d4"),
    ]
    for i, (t, c) in enumerate(lines):
        d.text((320, 60 + i * 24), f"{i + 1:>3}", font=font("consola.ttf", 17), fill="#858585")
        if c:
            d.text((370, 60 + i * 24), t, font=font("consola.ttf", 17), fill=c)
    d.rectangle([0, 876, 1600, 900], fill="#007acc")
    d.text((10, 878), "main  Ln 12, Col 8  UTF-8  C#", font=font("segoeui.ttf", 14), fill="white")
    return img


def invoice(ru):
    img = Image.new("RGB", (1240, 1500), "#e9e9e9")
    d = ImageDraw.Draw(img)
    d.rectangle([90, 40, 1150, 1460], fill="white")
    b, r = font("segoeuib.ttf", 40), font("segoeui.ttf", 22)
    if ru:
        d.text((140, 90), "Счёт на оплату № 128 от 12 октября 2026 г.", font=b, fill="#111")
        d.text((140, 170), "Поставщик: ООО «Ромашка», ИНН 7701234567", font=r, fill="#222")
        d.text((140, 205), "Покупатель: ИП Иванов И. И.", font=r, fill="#222")
        head = ["№", "Товары (работы, услуги)", "Кол-во", "Цена", "Сумма"]
        rows = [["1", "Разработка макета", "1", "30 000,00", "30 000,00"], ["2", "Вёрстка страниц", "3", "4 000,00", "12 000,00"],
                ["3", "Хостинг (12 мес.)", "1", "3 600,00", "3 600,00"]]
        total = "Итого к оплате: 45 600,00 ₽"
    else:
        d.text((140, 90), "INVOICE #2026-0412", font=b, fill="#111")
        d.text((140, 170), "Bill to: Northwind Traders, 42 Harbor St, Seattle WA", font=r, fill="#222")
        d.text((140, 205), "Date: October 12, 2026    Due: November 11, 2026", font=r, fill="#222")
        head = ["#", "Description", "Qty", "Unit price", "Amount"]
        rows = [["1", "Website design", "1", "$1,200.00", "$1,200.00"], ["2", "Page development", "3", "$400.00", "$1,200.00"],
                ["3", "Hosting (12 months)", "1", "$180.00", "$180.00"]]
        total = "Total due: $2,580.00"
    xs = [140, 200, 700, 820, 980]
    y = 300
    d.rectangle([130, y - 10, 1110, y + 34], fill="#f0f0f0")
    for x, h in zip(xs, head):
        d.text((x, y), h, font=font("segoeuib.ttf", 22), fill="#111")
    for row in rows:
        y += 56
        d.line([130, y - 12, 1110, y - 12], fill="#cccccc")
        for x, c in zip(xs, row):
            d.text((x, y), c, font=r, fill="#222")
    d.text((640, y + 90), total, font=font("segoeuib.ttf", 30), fill="#111")
    return img


def chart_bar():
    img, d = window(1400, 860, "Revenue dashboard — Excel")
    d.text((100, 70), "Revenue by month, 2026", font=font("segoeuib.ttf", 30), fill="#222")
    x0, y0 = 140, 760
    d.line([x0, 140, x0, y0], fill="#444", width=2)
    d.line([x0, y0, 1320, y0], fill="#444", width=2)
    vals = [120, 180, 150, 260, 310, 280, 390, 420, 360, 470, 510, 560]
    for i, (m, v) in enumerate(zip("Jan Feb Mar Apr May Jun Jul Aug Sep Oct Nov Dec".split(), vals)):
        x = x0 + 30 + i * 96
        d.rectangle([x, y0 - v, x + 60, y0], fill="#4472c4")
        d.text((x + 10, y0 + 8), m, font=font("segoeui.ttf", 18), fill="#333")
    for k in range(0, 601, 100):
        d.text((x0 - 60, y0 - k - 10), str(k), font=font("segoeui.ttf", 16), fill="#555")
        d.line([x0, y0 - k, 1320, y0 - k], fill="#e6e6e6")
    return img


def chart_line_ru():
    img = Image.new("RGB", (1200, 760), "white")
    d = ImageDraw.Draw(img)
    d.text((80, 30), "Продажи по неделям", font=font("segoeuib.ttf", 30), fill="#222")
    x0, y0 = 100, 680
    d.line([x0, 100, x0, y0], fill="#444", width=2)
    d.line([x0, y0, 1150, y0], fill="#444", width=2)
    rnd = random.Random(3)
    pts, v = [], 200
    for i in range(20):
        v = max(40, min(540, v + rnd.randint(-60, 80)))
        pts.append((x0 + 20 + i * 52, y0 - v))
    d.line(pts, fill="#e4572e", width=4)
    pts2 = [(x, y + 90 + rnd.randint(-20, 20)) for x, y in pts]
    d.line(pts2, fill="#29a5d6", width=4)
    for x, y in pts:
        d.ellipse([x - 5, y - 5, x + 5, y + 5], fill="#e4572e")
    d.text((900, 110), "— 2026   — 2025", font=font("segoeui.ttf", 20), fill="#333")
    return img


def map_img():
    img = Image.new("RGB", (1500, 950), "#f2efe9")
    d = ImageDraw.Draw(img)
    d.polygon([(0, 600), (400, 520), (800, 640), (1500, 560), (1500, 700), (800, 760), (400, 660), (0, 720)], fill="#aad3df")
    d.rectangle([900, 100, 1250, 380], fill="#c8e6a0")
    d.text((990, 220), "Central Park", font=font("segoeui.ttf", 22), fill="#3b6e22")
    for y in range(80, 950, 140):
        d.line([0, y, 1500, y + 40], fill="white", width=12)
    for x in range(100, 1500, 220):
        d.line([x, 0, x - 60, 950], fill="white", width=12)
    d.line([0, 300, 1500, 420], fill="#f7c66b", width=18)
    d.text((300, 330), "Main Street", font=font("segoeui.ttf", 20), fill="#444")
    d.text((520, 670), "River Neva", font=font("segoeuii.ttf", 22), fill="#3a7ca5")
    d.ellipse([640, 250, 680, 290], fill="#ea4335")
    d.polygon([(645, 280), (675, 280), (660, 315)], fill="#ea4335")
    d.rectangle([20, 20, 420, 70], fill="white", outline="#cccccc")
    d.text((36, 30), "Search Google Maps", font=font("segoeui.ttf", 20), fill="#777")
    d.text((1380, 880), "+  −", font=font("segoeuib.ttf", 28), fill="#555")
    return img


def chat(with_cat=False):
    img, d = window(1000, 1100, "Telegram")
    d.rectangle([0, 36, 1000, 100], fill="#ffffff")
    d.text((30, 52), "Аня", font=font("segoeuib.ttf", 24), fill="#111")
    d.rectangle([0, 100, 1000, 1030], fill="#d7e4c9")
    msgs = [(False, "Привет! Ты завтра будешь на встрече?"), (True, "Да, буду к десяти"),
            (False, "Отлично, захвати ноутбук, пожалуйста"), (True, "Хорошо 👍")]
    if with_cat:
        msgs = [(False, "Смотри, кто у нас теперь живёт!")]
    y = 130
    for mine, t in msgs:
        f = font("segoeui.ttf", 22)
        w = d.textlength(t, font=f) + 40
        x = 1000 - w - 30 if mine else 30
        d.rounded_rectangle([x, y, x + w, y + 56], 16, fill="#effdde" if mine else "white")
        d.text((x + 20, y + 13), t, font=f, fill="#111", embedded_color=True)
        y += 80
    if with_cat:
        cat = Image.open(os.path.join(OUT, "photo_cat_sitting.jpg")).convert("RGB")
        cat.thumbnail((560, 560))
        img.paste(cat, (30, y))
        y += cat.height + 20
        d.rounded_rectangle([1000 - 330, y, 970, y + 56], 16, fill="#effdde")
        d.text((1000 - 310, y + 13), "Какой милый котёнок!", font=font("segoeui.ttf", 22), fill="#111")
    d.rectangle([0, 1030, 1000, 1100], fill="white")
    d.text((30, 1050), "Написать сообщение…", font=font("segoeui.ttf", 22), fill="#999")
    return img


def error_dialog():
    img = Image.new("RGB", (1280, 800), "#2b5797")
    d = ImageDraw.Draw(img)
    x, y = 340, 260
    d.rectangle([x, y, x + 600, y + 230], fill="white", outline="#888")
    d.rectangle([x, y, x + 600, y + 34], fill="white")
    d.text((x + 12, y + 6), "Screeny.exe - Application Error", font=font("segoeui.ttf", 16), fill="#111")
    d.ellipse([x + 30, y + 70, x + 78, y + 118], fill="#e81123")
    d.text((x + 44, y + 74), "✕", font=font("segoeuib.ttf", 30), fill="white")
    d.multiline_text((x + 100, y + 66), "The application was unable to start correctly\n(0xc000007b). Click OK to close the application.",
                     font=font("segoeui.ttf", 17), fill="#111", spacing=6)
    d.rectangle([x, y + 170, x + 600, y + 230], fill="#f0f0f0")
    d.rectangle([x + 490, y + 184, x + 580, y + 214], fill="#e1e1e1", outline="#0078d7", width=2)
    d.text((x + 524, y + 188), "OK", font=font("segoeui.ttf", 16), fill="#111")
    return img


def login_form():
    img = Image.new("RGB", (1280, 860), "#f5f6f8")
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([440, 160, 840, 640], 12, fill="white", outline="#dddddd")
    d.text((560, 200), "Sign in", font=font("segoeuib.ttf", 34), fill="#111")
    for i, lab in enumerate(["Email", "Password"]):
        d.text((480, 290 + i * 100), lab, font=font("segoeui.ttf", 18), fill="#333")
        d.rounded_rectangle([480, 318 + i * 100, 800, 362 + i * 100], 6, outline="#bbbbbb", fill="white")
    d.text((496, 428), "••••••••", font=font("segoeui.ttf", 20), fill="#333")
    d.rounded_rectangle([480, 500, 800, 550], 6, fill="#2563eb")
    d.text((605, 510), "Log in", font=font("segoeuib.ttf", 20), fill="white")
    d.text((520, 580), "Forgot password?  Create account", font=font("segoeui.ttf", 16), fill="#2563eb")
    return img


def text_doc(title, body, app="Блокнот"):
    img, d = window(1300, 820, f"{title[:40]} — {app}")
    d.text((40, 70), title, font=font("segoeuib.ttf", 30), fill="#111")
    d.multiline_text((40, 130), body, font=font("segoeui.ttf", 22), fill="#222", spacing=10)
    return img


def desktop(w, h, app, sidebar, title, lines, seed):
    """A desktop-size window: title bar, a sidebar list, a toolbar, small (14 px) body text, Windows taskbar."""
    img, d = window(w, h, app)
    f, fb, fs = font("segoeui.ttf", 14), font("segoeuib.ttf", 18), font("segoeui.ttf", 13)
    d.rectangle([0, 36, w, 76], fill="#f7f7f7")
    for i, t in enumerate(["File", "Edit", "View", "Tools", "Help"]):
        d.text((16 + i * 60, 48), t, font=fs, fill="#333")
    d.rectangle([0, 76, 300, h - 48], fill="#f3f3f3")
    for i, t in enumerate(sidebar):
        d.text((20, 96 + i * 26), t, font=fs, fill="#222")
    d.text((340, 100), title, font=fb, fill="#111")
    for i, t in enumerate(lines):
        d.text((340, 140 + i * 22), t, font=f, fill="#222")
    rnd = random.Random(seed)
    y = 160 + len(lines) * 22
    while y < h - 80:  # filler paragraphs of neutral text
        d.text((340, y), " ".join(rnd.choice(["data", "item", "note", "value", "list", "page", "item", "view", "text"])
                                   for _ in range(rnd.randint(8, 18))), font=f, fill="#555")
        y += 22
    d.rectangle([0, h - 48, w, h], fill="#202020")
    d.text((w - 120, h - 34), "12:41  RU", font=fs, fill="white")
    return img


def spreadsheet():
    img, d = window(1500, 900, "Книга1 — Excel")
    cols = ["", "A", "B", "C", "D", "E", "F"]
    cw = 210
    for r in range(28):
        for c in range(7):
            x, y = 20 + c * cw if c else 20, 60 + r * 28
            x = 20 + (c - 1) * cw + 50 if c else 20
            w = cw if c else 50
            d.rectangle([x, y, x + w, y + 28], outline="#d4d4d4", fill="#f3f3f3" if r == 0 or c == 0 else "white")
            if r == 0 and c:
                d.text((x + 95, y + 4), cols[c], font=font("segoeui.ttf", 16), fill="#333")
            elif c == 0 and r:
                d.text((x + 14, y + 4), str(r), font=font("segoeui.ttf", 16), fill="#333")
            elif r == 1 and c:
                d.text((x + 8, y + 4), ["Регион", "Январь", "Февраль", "Март", "Апрель", "Итого"][c - 1], font=font("segoeuib.ttf", 16), fill="#111")
            elif r > 1 and c:
                rnd = random.Random(r * 10 + c)
                t = ["Москва", "Казань", "Самара", "Пермь", "Омск", "Тверь"][r % 6] if c == 1 else f"{rnd.randint(1000, 99999):,}".replace(",", " ")
                d.text((x + 8, y + 4), t, font=font("segoeui.ttf", 16), fill="#222")
    return img


def main():
    os.makedirs(OUT, exist_ok=True)
    for name, (_, url) in PHOTOS.items():
        path = os.path.join(OUT, name)
        if not os.path.exists(path):
            req = urllib.request.Request(url, headers={"User-Agent": "ScreenySpike/0.1 (semantic search research)"})
            with urllib.request.urlopen(req) as r, open(path, "wb") as f:
                f.write(r.read())
            time.sleep(3)  # Commons rate-limits bursts (HTTP 429)
    gen = {
        "code_editor.png": code_editor, "invoice_ru.png": lambda: invoice(True), "invoice_en.png": lambda: invoice(False),
        "chart_bar.png": chart_bar, "chart_line_ru.png": chart_line_ru, "map.png": map_img, "chat.png": lambda: chat(False),
        "chat_with_cat.png": lambda: chat(True), "error_dialog.png": error_dialog, "login_form.png": login_form,
        "spreadsheet.png": spreadsheet,
        "doc_lease_ru.png": lambda: text_doc("Договор аренды квартиры № 17", "\n".join([
            "г. Москва, 1 сентября 2026 г.", "",
            "Арендодатель передаёт, а Арендатор принимает во временное пользование",
            "жилое помещение по адресу: ул. Лесная, д. 5, кв. 12.",
            "Срок аренды — 11 месяцев. Ежемесячная плата — 55 000 руб.",
            "Залог — 55 000 руб. Оплата до 5 числа каждого месяца."]), "Word"),
        "doc_recipe_ru.png": lambda: text_doc("Борщ по-домашнему", "\n".join([
            "Ингредиенты: свёкла 2 шт., капуста 300 г, картофель 3 шт., морковь 1 шт.,",
            "лук 1 шт., томатная паста 2 ст. л., говядина 500 г, соль, лавровый лист.", "",
            "1. Сварите бульон из говядины (1,5 часа).", "2. Нарежьте овощи соломкой.",
            "3. Обжарьте лук, морковь и свёклу с томатной пастой.",
            "4. Добавьте картофель и капусту, варите 20 минут."])),
        "email_flight_en.png": lambda: text_doc("Your trip is confirmed: Berlin → Lisbon", "\n".join([
            "Booking reference: K7Q2XP", "Passenger: Alex Morgan", "",
            "FRI 14 NOV  BER 07:25 → LIS 10:05   TP 539  Economy",
            "SUN 23 NOV  LIS 18:40 → BER 23:10   TP 540  Economy", "",
            "Check-in opens 24 hours before departure.", "Baggage: 1 × 23 kg checked bag included."]), "Outlook"),
        "note_wifi_en.png": lambda: text_doc("home stuff", "\n".join([
            "Wi-Fi network: Morgan_5G", "Password: blue-otter-2931", "",
            "Router admin: 192.168.1.1", "ISP support: +1 800 555 0199"]), "Notepad"),
        "desk_mail_hotel.png": lambda: desktop(2560, 1440, "Inbox - alex@example.com - Mail",
            ["Inbox (12)", "Sent", "Drafts", "Archive", "", "Today", "  Team standup notes", "  Hotel Aurora", "  Invoice #44"],
            "Hotel Aurora: your reservation is confirmed",
            ["Dear Alex Morgan,", "Thank you for booking with Hotel Aurora, Kazan. Your reservation number is HA-55821.",
             "Check-in: Thu 4 Dec 2026, from 14:00. Check-out: Sun 7 Dec 2026, until 12:00. 3 nights, 1 double room.",
             "Total: 21 600 RUB, pay at the hotel. Free cancellation until 2 Dec 2026."], 1),
        "desk_browser_tax.png": lambda: desktop(1920, 1080, "Личный кабинет налогоплательщика — Microsoft Edge",
            ["Главная", "Объекты", "Начисления", "Платежи", "Документы", "Сообщения"],
            "Налоговое уведомление № 12345678 за 2025 год",
            ["Транспортный налог: автомобиль легковой, 150 л.с., налоговая ставка 35 руб.", "Сумма к уплате: 5 250 руб.",
             "Налог на имущество физических лиц: квартира, кадастровая стоимость 6 100 000 руб.", "Сумма к уплате: 1 830 руб.",
             "Срок уплаты: не позднее 1 декабря 2026 г."], 2),
        "desk_settings_vpn.png": lambda: desktop(1920, 1080, "Settings",
            ["System", "Bluetooth & devices", "Network & internet", "Personalization", "Apps", "Accounts"],
            "Network & internet > VPN > Office VPN",
            ["Connection name: Office VPN", "Server name or address: vpn.example.com", "VPN type: L2TP/IPsec with pre-shared key",
             "Type of sign-in info: User name and password", "Connect automatically: Off   Allow VPN over metered networks: On"], 3),
        "desk_pdf_warranty.png": lambda: desktop(2560, 1440, "garantiya.pdf - Adobe Acrobat Reader",
            ["Страница 1", "Страница 2", "Страница 3"],
            "Гарантийный талон",
            ["Изделие: стиральная машина, модель WM-7120, серийный номер 7120-55-0193",
             "Дата продажи: 15 марта 2026 г.  Гарантийный срок: 24 месяца со дня продажи.",
             "Гарантия не распространяется на повреждения, вызванные нарушением правил эксплуатации.",
             "Продавец: ООО «Техномир», подпись и печать продавца."], 4),
        "desk_news.png": lambda: desktop(1920, 1080, "News - Microsoft Edge",
            ["Top stories", "World", "Sport", "Weather", "Science"],
            "Weekend weather: rain moves east, sunny on Sunday",
            ["Local football club wins 2-1 after a late goal in the derby.", "Scientists map a new coral reef off the coast.",
             "City council approves a new bike lane on the river embankment."], 5),
    }
    for name, fn in gen.items():
        fn().save(os.path.join(OUT, name))
    images = sorted(list(PHOTOS) + list(gen))
    with open(os.path.join(OUT, "testset.json"), "w", encoding="utf-8") as f:
        json.dump({"images": images, "queries": QUERIES, "photo_sources": {k: v[0] for k, v in PHOTOS.items()}}, f,
                  ensure_ascii=False, indent=1)
    print(f"{len(images)} images, {len(QUERIES)} queries -> {OUT}")


if __name__ == "__main__":
    main()
