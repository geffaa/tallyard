# Tallyard — Sistem Operasional Distributor (Inventory · Purchasing · Order-to-Cash + AI)

## 1. Ringkasan

Tallyard adalah sistem operasional untuk distributor/grosir kecil–menengah (5–200 karyawan, 1–10 gudang) yang selama ini bekerja dengan Excel dan WhatsApp. Tallyard mencakup tiga alur yang paling sering membuat bisnis seperti ini rugi:
1. **Stok multi-gudang yang akurat dan real-time**, dengan ledger yang tidak bisa diubah dan jaminan stok tidak minus walau banyak transaksi masuk bersamaan.
2. **Pembelian:** reorder point → draft PO → approval → penerimaan barang.
3. **Penjualan:** sales order → reservasi stok → pengiriman → invoice → pembayaran.

Di atasnya ada lapisan AI: invoice/PO PDF dari supplier dibaca menjadi draft dokumen, staf bisa bertanya ke data dengan bahasa biasa, dan sistem menyarankan kapan perlu reorder.

Ini **bukan ERP penuh** (tidak ada akuntansi buku besar, HR, atau payroll). Fokusnya alur kerja sempit yang paling menyakitkan, sesuai riset pasar: di Upwork, job ERP penuh dikuasai Odoo, sementara job "custom inventory/order management" terus muncul. Proyek ini juga portfolio Upwork yang menyambung ke profil "RAG & LLM Engineer" (Telusur).

## 2. Masalah

- Stok dicatat di spreadsheet per gudang. Tidak ada yang tahu angka **on-hand / dipesan / tersedia** secara real-time, sehingga barang yang dijual ternyata kosong, atau sebaliknya stok menumpuk.
- Data diinput dua kali (WA → Excel → nota), sering salah, dan tidak ada jejak siapa mengubah apa.
- Reorder berdasarkan ingatan pemilik. Stockout baru ketahuan ketika pelanggan komplain.
- Invoice dan PO dari supplier datang sebagai PDF atau foto, lalu diketik ulang manual.
- NetSuite/SAP terlalu mahal dan lama diimplementasikan. Odoo murah tapi alurnya kaku dan sering butuh konsultan. Aplikasi inventory SaaS kecil tidak menangani approval, multi-gudang, atau batch/expiry dengan baik.

## 3. Target user

- **Segmen pertama:** distributor FMCG, bahan bangunan, farmasi/alkes, dan suku cadang dengan 100–5.000 SKU.
- **Peran di dalam produk:**
  - *Owner/Admin*: mengatur pengguna, gudang, approval, melihat dashboard.
  - *Purchasing*: membuat PO dan mengelola supplier.
  - *Warehouse*: penerimaan barang, picking, transfer, stock opname.
  - *Sales*: membuat sales order dan melihat stok tersedia.
  - *Finance*: invoice, pembayaran, piutang/utang.
- **Pembeli di Upwork:** bisnis yang memposting "custom inventory / order management system", agency yang butuh backend operasional untuk toko online, dan klien yang ingin integrasi stok dengan Shopify/Odoo.

## 4. Asumsi

1. ⚠️ **Stack: ASP.NET Core (.NET 10 LTS) modular monolith + EF Core + Postgres 16, frontend Angular 20 SPA.** Dipilih murni karena kecocokan teknis dengan domain (bukan pasar); perbandingan lengkap dengan alternatif ada di §10.1.
2. Multi-tenant memakai **shared database + `tenant_id` + Postgres RLS**, pola yang sama dengan Telusur (sudah terbukti).
3. ⚠️ **Model stok berbasis ledger:** setiap perubahan stok adalah baris `stock_movements` yang append-only. Saldo (`stock_levels`) adalah proyeksi yang diperbarui dalam transaksi yang sama, dengan `SELECT … FOR UPDATE` dan `CHECK (on_hand >= 0)`. Koreksi dilakukan dengan movement pembalik, bukan UPDATE.
4. Penilaian persediaan memakai **moving average cost** (paling umum di distributor Indonesia). FIFO masuk "Nanti".
5. Mata uang tunggal per tenant (demo: USD; IDR dan lainnya bisa dipilih) plus pajak penjualan/VAT yang bisa diatur per tenant dan per item (mis. PPN 11% di Indonesia). Multi-currency masuk "Nanti".
6. ⚠️ **AI gratis lewat Groq:** `gpt-oss-120b` untuk ekstraksi terstruktur dan text-to-SQL, serta model vision Groq (Llama 4 Scout) untuk foto/scan. PDF teks dibaca dulu dengan `pdftotext`, lalu LLM hanya menstrukturkan. *Risiko:* batas free tier dan ketersediaan model vision bisa berubah, jadi cek dulu.
7. Chat ke data **tidak menjalankan SQL bebas dari LLM**. LLM memilih dari sekumpulan *tools* yang sudah ditentukan (stok per gudang, penjualan per periode, barang hampir habis, piutang jatuh tempo), dan semua query tetap berjalan di bawah RLS dengan role database read-only.
8. Barcode memakai kamera HP lewat browser (ZXing). Hardware scanner bekerja sebagai keyboard, jadi otomatis didukung.
9. Dokumen PDF (PO, surat jalan, invoice) dibuat di server dengan QuestPDF (lisensi Community gratis untuk usaha dengan pendapatan < $1 juta/tahun).
10. Data demo sepenuhnya fiktif: distributor "Harbor & Pine Supply Co.", 3 gudang, sekitar 400 SKU, 6 bulan histori transaksi hasil generator.

## 5. Scope

**MVP**
1. Tenant, user, peran, gudang, dan audit log
2. Master data: produk (varian, satuan + konversi, barcode), supplier, pelanggan, daftar harga
3. Inventory: ledger, saldo per gudang, transfer antar gudang, adjustment, stock opname, batch/expiry
4. Purchasing: PO, approval bertingkat, penerimaan barang (parsial), retur ke supplier
5. Order-to-cash: sales order, reservasi stok, picking/pengiriman (parsial), invoice, pembayaran, piutang
6. Reorder: min/max per gudang, saran PO otomatis
7. AI: dokumen supplier (PDF/foto) → draft PO/penerimaan, chat ke data, catatan penjelasan untuk saran reorder
8. Import dari Excel (produk, saldo awal, pelanggan) dengan validasi per baris
9. Dashboard dan laporan: nilai persediaan, kartu stok, penjualan, umur piutang
10. Dua bahasa (Inggris/Indonesia)

**Nanti**
- Integrasi Shopify (sinkron stok dan order) dan Odoo (connector)
- FIFO, multi-currency, landed cost
- Akuntansi buku besar (jurnal otomatis)
- Aplikasi mobile native, mode offline gudang
- Forecasting permintaan berbasis model (bukan hanya rata-rata bergerak)
- e-Faktur pajak

## 6. Fitur MVP secara detail

### 6.1 Tenant, user, peran, audit
- Peran tetap (Owner, Purchasing, Warehouse, Sales, Finance) plus pembatasan per gudang (staf gudang Chicago hanya melihat gudang Chicago).
- Audit log append-only untuk semua dokumen dan perubahan master data (siapa, kapan, sebelum/sesudah), dipartisi per bulan seperti Telusur.
- *Acceptance:* staf Warehouse tidak bisa melihat harga beli, dan staf gudang A tidak melihat stok gudang B (dites).

### 6.2 Master data
- Produk dengan SKU, varian, satuan dasar + konversi (1 dus = 24 pcs), beberapa barcode, kategori, harga beli terakhir, HPP rata-rata, dan flag "lacak batch/expiry".
- Daftar harga per grup pelanggan, termasuk harga berjenjang berdasarkan kuantitas.
- *Acceptance:* SO dalam satuan dus mengurangi stok dalam pcs dengan benar.

### 6.3 Inventory (inti)
- Jenis movement: receipt, issue, transfer out/in, adjustment, opname, return.
- Tiga angka per produk × gudang: **on-hand**, **reserved** (dipesan SO), dan **available = on-hand − reserved**. Ditambah **incoming** dari PO yang belum diterima.
- Transfer antar gudang dengan status "dalam perjalanan" (stok keluar dari A, belum masuk ke B).
- Stock opname: snapshot → hitung (bisa lewat scan HP) → selisih → approval → movement adjustment.
- Batch/expiry: penerimaan mencatat batch dan tanggal kedaluwarsa, pengeluaran memakai FEFO (yang paling cepat kedaluwarsa keluar dulu).
- Kartu stok per produk: setiap movement dengan saldo berjalan dan link ke dokumen sumber.
- *Acceptance:* 50 request pengeluaran stok paralel untuk barang dengan stok 10 menghasilkan tepat 10 yang sukses dan tidak ada stok minus (tes konkurensi). Saldo proyeksi selalu sama dengan jumlah ledger (tes invariant).

### 6.4 Purchasing
- PO: draft → menunggu approval → disetujui → dikirim ke supplier (PDF/email) → diterima sebagian/penuh → ditutup.
- Aturan approval berdasarkan nilai (misalnya > $5.000 perlu Owner) dan bisa bertingkat.
- Penerimaan barang terhadap PO: kuantitas diterima ≤ dipesan + toleransi, batch/expiry, dan memperbarui HPP rata-rata.
- Retur ke supplier.
- *Acceptance:* PO 100 pcs yang diterima 60 lalu 40 berubah status dengan benar, dan HPP dihitung ulang di setiap penerimaan.

### 6.5 Order-to-cash
- SO: draft → dikonfirmasi (stok direservasi) → di-pick → dikirim (surat jalan) → diinvoice → dibayar.
- Konfirmasi gagal dengan pesan jelas jika stok tersedia kurang, dengan opsi backorder.
- Batas kredit pelanggan: SO ditahan jika piutang + nilai SO melebihi limit, kecuali di-override Finance (tercatat di audit).
- Invoice dari pengiriman (bisa parsial), pembayaran parsial, dan laporan umur piutang (0–30, 31–60, 61–90, > 90 hari).
- *Acceptance:* pembatalan SO melepas reservasi, dan pengiriman parsial hanya menginvoice yang terkirim.

### 6.6 Reorder
- Min/max atau reorder point per produk × gudang. Job harian menghitung barang di bawah reorder point, dengan memperhitungkan stok tersedia + incoming.
- Saran PO dikelompokkan per supplier, lalu bisa dibuat menjadi PO dengan satu klik.
- Saran reorder point dari rata-rata penjualan × lead time + safety stock, dengan penjelasan singkat oleh LLM ("rata-rata terjual 12/hari, lead time supplier 5 hari").

### 6.7 AI
- **Dokumen supplier → draft:** upload PDF/foto invoice atau penawaran. Sistem mengekstrak supplier, nomor, tanggal, dan baris item (nama, qty, satuan, harga), mencocokkan item ke SKU (exact match pada kode/barcode, lalu kemiripan trigram, lalu LLM memilih dari kandidat teratas), dan menampilkan layar review berdampingan dengan PDF. Field yang kurang yakin ditandai kuning. **Tidak ada yang tersimpan tanpa konfirmasi manusia.**
- **Chat ke data:** "barang apa yang akan habis minggu depan di gudang Chicago?" LLM memanggil tools yang sudah ditentukan, menjawab dengan angka, dan menampilkan tabel serta link ke data sumbernya.
- **Evaluasi:** set uji berisi 30 dokumen supplier fiktif dengan jawaban yang benar, untuk mengukur akurasi per field dan akurasi pencocokan SKU (pola evaluasi dari Telusur).
- *Acceptance:* akurasi baris item ≥ 90% pada set uji, dan chat tidak pernah menampilkan data gudang yang tidak boleh dilihat penanya.

### 6.8 Import Excel
- Template produk, saldo awal, pelanggan, dan supplier. Pratinjau dengan error per baris ("baris 14: satuan 'dos' tidak dikenal"). Import berjalan di queue.

### 6.9 Dashboard dan laporan
- Nilai persediaan per gudang, barang hampir habis, barang tidak bergerak > 90 hari, penjualan per periode, piutang jatuh tempo, dan PO yang terlambat diterima.
- Export ke Excel.

## 7. User flow

**Pembelian dari dokumen supplier**
1. Purchasing mengunggah PDF penawaran supplier.
2. AI mengisi draft PO, lalu Purchasing mengoreksi dua item yang ditandai kuning dan mengirim untuk approval.
3. Owner menyetujui dari notifikasi, lalu PDF PO terkirim ke supplier.
4. Barang datang. Staf gudang membuka PO di HP, memindai barcode, mengisi batch/expiry, dan menerima 60 dari 100.
5. Stok on-hand bertambah, HPP diperbarui, dan PO berstatus "diterima sebagian".

**Penjualan**
1. Sales membuat SO dan melihat stok tersedia per gudang saat mengetik.
2. Konfirmasi mereservasi stok (atau ditahan karena limit kredit).
3. Gudang mendapat daftar picking (urut FEFO), memindai barang, dan mencetak surat jalan.
4. Finance membuat invoice dari pengiriman lalu mencatat pembayaran.

## 8. Halaman / layar

- Dashboard
- Produk (daftar, detail, kartu stok), Stok (matriks produk × gudang)
- Transfer, Adjustment, Stock opname (dengan mode scan HP)
- Supplier, Purchase order, Penerimaan barang, Saran reorder
- Pelanggan, Sales order, Pengiriman, Invoice, Pembayaran, Umur piutang
- Inbox dokumen AI (upload + review berdampingan), Chat data
- Import, Laporan
- Pengaturan: pengguna, gudang, aturan approval, pajak; Audit log

## 9. Data model

- `tenants`, `users`, `user_warehouse` (akses per gudang), `audit_logs` (partisi bulanan)
- `warehouses`, `locations` (rak, opsional)
- `products`, `product_units` (konversi), `product_barcodes`, `price_lists`, `price_list_items`
- `batches` (product, nomor, expiry)
- `stock_movements` (append-only: tenant, product, warehouse, batch, qty bertanda, unit_cost, `source_type`/`source_id`, created_by)
- `stock_levels` (product × warehouse × batch: on_hand, reserved, avg_cost; `CHECK on_hand >= 0`, `CHECK reserved <= on_hand`)
- `suppliers`, `purchase_orders`, `purchase_order_lines`, `goods_receipts`, `goods_receipt_lines`
- `approval_rules`, `approvals`
- `customers` (limit kredit), `sales_orders`, `sales_order_lines`, `reservations`, `shipments`, `shipment_lines`, `invoices`, `invoice_lines`, `payments`, `payment_allocations`
- `transfers`, `stock_counts`, `stock_count_lines`
- `reorder_rules`, `reorder_suggestions`
- `ai_documents` (file, status, hasil ekstraksi JSON, confidence, dokumen hasil), `ai_chat_messages`
- `document_sequences` (nomor dokumen PO-2026-000123 tanpa celah, per tenant per tipe)

Semua tabel tenant memakai RLS. Semua angka uang memakai `numeric(18,2)` dan kuantitas `numeric(18,4)`, tidak ada float.

## 10. Arsitektur & tech stack

| Bagian | Teknologi |
|---|---|
| API | ASP.NET Core (.NET 10), Minimal API per modul (vertical slice), EF Core + Npgsql, FluentValidation, OpenAPI |
| Frontend | Angular 20 (standalone components, signals, typed reactive forms), spartan/ui + Tailwind, AG Grid Community, klien API ter-generate dari OpenAPI; di-build sebagai SPA statis dan di-serve oleh ASP.NET (satu origin) |
| Database | Postgres 16 (RLS, row lock, constraint), `pg_trgm` untuk pencocokan nama barang (fuzzy) |
| Background job & realtime | Hangfire (Postgres storage) untuk job reorder/import/AI; SignalR untuk update stok live |
| AI | Groq (`gpt-oss-120b`, vision model) lewat adapter provider; pencocokan SKU: kode/barcode exact → `pg_trgm` → LLM memilih dari 5 kandidat teratas |
| PDF | QuestPDF untuk dokumen keluar, PdfPig untuk membaca teks dokumen masuk |
| Tes | xUnit + Testcontainers (Postgres asli, termasuk tes konkurensi dan RLS) |
| Demo | Puppeteer (rekaman), GitHub Pages (halaman studi kasus) |

### 10.1 Kenapa arsitektur ini (dibandingkan alternatifnya)

Karakter domain yang menentukan pilihan: puluhan entitas yang saling terkait, aturan bisnis ketat (stok tidak boleh minus, state machine dokumen, approval), konkurensi pada baris stok yang sama, form panjang dengan baris item dinamis, dan tabel data besar. Trafiknya kecil (puluhan pengguna per tenant), tetapi kebenaran data adalah segalanya.

| Keputusan | Dipilih | Alternatif & kenapa tidak |
|---|---|---|
| Bentuk sistem | **Modular monolith**, satu deployable, modul Inventory / Purchasing / Sales / AI dengan batas yang ditegakkan (tes arsitektur) | *Microservices:* transaksi lintas modul (SO mereservasi stok) jadi saga terdistribusi tanpa manfaat skala. *Monolith tanpa batas:* cepat kusut. |
| Backend | **ASP.NET Core + C#** | *Laravel/PHP:* produktif, tetapi tipe lemah untuk domain model besar; refactor aturan bisnis lebih berisiko. *Go:* unggul di service I/O dan konkurensi, tetapi untuk domain CRUD kaya entitas tidak ada ORM/unit-of-work yang setara, sehingga boilerplate besar. *Node/NestJS:* tipe TypeScript hilang di runtime, desimal butuh library. *Python/FastAPI:* cocok untuk ML (Telusur), lemah untuk domain transaksional besar. C# punya `decimal` native (uang tanpa float), tipe kuat sampai runtime, dan EF Core (unit of work, concurrency token, migrasi) yang matang. |
| Database | **Postgres** | *MySQL:* tidak ada RLS, constraint/`CHECK` dan partisi lebih lemah. *SQL Server:* setara secara fitur, tetapi berbayar di produksi dan RLS-nya kurang ergonomis untuk multi-tenant. *MongoDB:* data relasional dengan invariant lintas tabel justru butuh transaksi dan constraint. |
| Model stok | **Ledger append-only + proyeksi saldo dalam satu transaksi, `SELECT … FOR UPDATE`** | *Event sourcing penuh:* kompleksitas (rebuild, versioning event) tanpa kebutuhan nyata; ledger sudah memberi histori lengkap. *Optimistic concurrency saja:* pada barang laris, retry storm; pessimistic lock per baris stok lebih tepat. |
| Frontend | **Angular SPA** | *React (Vite/Next):* bagus, tetapi form kompleks, DI, routing guard, dan i18n semuanya library pihak ketiga yang harus dirakit. Angular menyediakan typed reactive forms (`FormArray` untuk baris PO/SO), DI, dan struktur yang seragam secara bawaan; inilah bentuk aplikasi Angular (form-berat, dashboard internal). *Next.js:* SSR/SEO tidak berguna di balik login dan menambah server kedua. *Blazor:* satu bahasa, tetapi ekosistem komponen tabel/form dan ukuran payload WASM lebih lemah. |
| Penyajian | **SPA statis di-serve ASP.NET, satu origin, cookie httpOnly** | Dua origin = CORS + cookie lintas domain + dua deployment tanpa manfaat. |
| Job | **Hangfire di Postgres** | *Redis/RabbitMQ:* infrastruktur tambahan untuk volume job yang kecil; satu database lebih mudah dioperasikan dan di-backup. |
| Realtime | **SignalR** | *Polling:* matriks stok terlihat basi saat gudang sibuk. *WebSocket manual:* reconnect/grouping per tenant harus dibuat sendiri. |
| API contract | **OpenAPI → klien TypeScript ter-generate** | Tipe tulis tangan cepat menyimpang dari backend. |

Klaim di atas dibuktikan di repo, tidak hanya ditulis: tes konkurensi stok, tes RLS antar tenant, tes arsitektur batas modul, dan load test terukur di README.

Prinsip:
- Semua perubahan stok lewat **satu service** (`StockLedger.PostAsync()`) yang mengunci baris saldo, menulis movement, dan memperbarui proyeksi dalam satu transaksi. Tidak ada kode lain yang boleh menulis `stock_levels`.
- Dokumen (PO, SO, dan lainnya) adalah state machine eksplisit. Transisi yang tidak valid ditolak di domain, bukan hanya di UI.
- Penomoran dokumen tanpa celah memakai baris counter yang dikunci dalam transaksi yang sama.
- Idempotency key pada aksi yang mengubah stok, agar double-click atau retry tidak memposting dua kali.

## 11. Milestone & task

**M1 — Fondasi (±3 hari)** ✅ selesai
- Setup solusi .NET (modular monolith) + Angular + Docker (Postgres), auth, tenant + RLS, peran, akses per gudang, audit log
- Master data produk/satuan/barcode, gudang, supplier, pelanggan
- Generator data demo

**M2 — Inventory inti (±3 hari)** ✅ selesai
- `StockLedger`, saldo, kartu stok, adjustment, transfer, batch/FEFO
- Tes konkurensi dan tes invariant ledger
- Matriks stok dan update live (SignalR)

**M3 — Purchasing (±2–3 hari)** ✅ selesai
- PO + approval, PDF PO, penerimaan parsial, HPP rata-rata, retur, saran reorder

**M4 — Order-to-cash (±3 hari)**
- SO + reservasi + limit kredit, picking/pengiriman, surat jalan, invoice, pembayaran, umur piutang

**M5 — AI (±3 hari)**
- Ekstraksi dokumen supplier + layar review + SKU matching
- Chat data dengan tools
- Set uji dan hasil evaluasi di README

**M6 — Polish & portfolio (±2 hari)**
- Import Excel, dashboard, laporan, opname dengan scan HP
- i18n, redesign "anti slop", README + studi kasus, rekaman demo Puppeteer, push ke GitHub

## 12. Risiko & pertanyaan terbuka

- **Scope creep ke ERP penuh.** Akuntansi, HR, dan manufaktur sengaja tidak dikerjakan. Fokus ke kedalaman tiga alur inti.
- **Akurasi ekstraksi AI** pada foto buram atau format invoice yang beragam. Mitigasi: layar review wajib, confidence per field, dan set uji yang terukur.
- **Free tier Groq** (rate limit, model vision bisa berubah). Mitigasi: adapter provider, seperti di Telusur.
- **Moving average vs FIFO:** sebagian klien mungkin mewajibkan FIFO. Data model sudah menyimpan cost per movement, jadi FIFO bisa ditambahkan nanti.
- **Pertanyaan:** perlu tampilan khusus untuk satu vertikal (misalnya farmasi dengan expiry ketat), atau tetap umum? Rekomendasi: tetap umum, dengan data demo FMCG.
