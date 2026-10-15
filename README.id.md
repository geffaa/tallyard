# Tallyard

[English](README.md) · **Bahasa Indonesia**

Sistem operasional untuk distributor/grosir: inventory multi-gudang, pembelian, dan
order-to-cash, dengan lapisan AI yang mengubah dokumen supplier menjadi draft pesanan. Sengaja
bukan ERP penuh (tanpa buku besar, HR, atau payroll); fokus mendalam di tiga alur yang paling
sering membuat distributor rugi. Rencana lengkap: [BREAKDOWN.md](BREAKDOWN.md).

**Status:** milestone 1 dari 6 selesai: tenant terisolasi dengan row-level security Postgres,
peran dan akses per gudang, audit log append-only, dan data master (produk dengan kemasan dan
barcode, gudang, supplier, pelanggan). Berikutnya: ledger stok (M2).

## Stack

| Bagian | Teknologi |
|---|---|
| API | ASP.NET Core (.NET 10), minimal API per modul, EF Core + Npgsql, FluentValidation, OpenAPI |
| Web | Angular 20 (standalone components, signals, typed reactive forms, zoneless), Tailwind CSS 4 |
| Database | Postgres 16: row-level security, constraint, pencarian fuzzy `pg_trgm`, partisi bulanan |
| Tes | xUnit + Testcontainers (Postgres asli), NetArchTest untuk batas modul |

Satu deployable: build Angular disajikan oleh ASP.NET dari origin yang sama, sehingga cookie sesi
bisa `HttpOnly` + `SameSite=Strict` tanpa CORS. Alasan memilih stack ini dibanding Laravel, Go,
Node, atau React ada di [BREAKDOWN.md §10.1](BREAKDOWN.md#101-kenapa-arsitektur-ini-dibandingkan-alternatifnya).

## Menjalankan secara lokal

Butuh Docker, .NET 10 SDK, dan Node 20+.

```bash
docker compose up -d                          # Postgres di :5442
cd src/Tallyard.Api
dotnet run -- seed                            # migrasi + data demo (2 tenant)
dotnet run                                    # API di http://localhost:5080

cd ../../web
npm install
npm start                                     # http://localhost:4210 (proxy /api ke :5080)
```

Atau build web ke dalam API (`npm run build` di `web/`) lalu buka http://localhost:5080.

Login demo: workspace `harborpine`, password `tallyard123`:

| Email | Peran | Yang bisa dicoba |
|---|---|---|
| `owner@harborpine.test` | Owner | semuanya; pengguna, audit log |
| `purchasing@harborpine.test` | Pembelian | mengubah produk dan supplier |
| `warehouse@harborpine.test` | Gudang | hanya melihat gudang Chicago; data master read-only |
| `sales@harborpine.test` | Sales | mengubah pelanggan dan limit kredit |

Tenant kedua, `northpeak` (`owner@northpeak.test`), terisolasi penuh. Semua perusahaan dan produk
fiktif.

## Tes

```bash
dotnet test        # butuh Docker: menjalankan Postgres sendiri
```

Tes berjalan di Postgres asli yang disiapkan persis seperti produksi (script init yang sama;
aplikasi terhubung sebagai role yang bukan pemilik tabel):

- **Isolasi tenant di database:** tanpa tenant, `SELECT count(*) FROM products` menghasilkan 0;
  insert baris untuk tenant lain ditolak oleh policy.
- **Audit log append-only:** `UPDATE`, `DELETE`, dan `TRUNCATE` gagal bahkan untuk pemilik tabel;
  role aplikasi sama sekali tidak punya hak `UPDATE`.
- **Akses:** staf gudang hanya melihat gudangnya; menonaktifkan pengguna langsung mengakhiri
  sesinya di request berikutnya; Owner terakhir tidak bisa diturunkan perannya.
- **Integritas data:** optimistic concurrency (menimpa perubahan orang lain ditolak), satu barcode
  hanya untuk satu produk, perubahan anak (barcode, kemasan) tercatat di riwayat produk.
- **Batas modul:** setiap modul hanya boleh bergantung pada modul yang diizinkan.

## Keputusan desain

- **Row-level security, bukan hanya `WHERE tenant_id`.** Setiap koneksi yang dibuka EF mengatur
  `app.tenant_id` (`Infrastructure/TenantConnectionInterceptor.cs`), termasuk ke kosong, sehingga
  koneksi dari pool tidak pernah membawa tenant request sebelumnya. RLS aktif tetapi tidak
  di-force: role owner menjalankan migrasi dan seeding, role aplikasi selalu difilter.
- **Id UUIDv7 dibuat aplikasi.** Diketahui sebelum insert (dirujuk audit) dan berurutan waktu
  (ramah B-tree). EF diberi tahu bahwa database tidak pernah membuatnya; tanpa itu, anak baru
  pada parent yang sudah dimuat dikira baris lama.
- **Audit di transaksi yang sama.** Interceptor `SaveChanges` menulis satu baris audit per data
  yang berubah sebelum disimpan, sehingga perubahan dan auditnya tersimpan bersama atau tidak
  sama sekali. Perubahan barcode dan kemasan dicatat di entri produknya.
- **Sesi dicek di setiap request.** Cookie berisi user, tenant, dan peran; setiap request
  memastikan user masih aktif dan perannya masih sama, sehingga pencabutan akses langsung berlaku.
- **Permission, bukan peran, di kode.** Endpoint mensyaratkan permission (`EditCatalog`); satu
  tabel memetakan permission ke peran. Web menerima daftar yang sama untuk menyembunyikan yang
  tidak bisa dipakai; API tetap menegakkannya.
- **Error dengan kode tetap, dalam bahasa request.** `{ code, detail, errors }`: `detail` mengikuti
  `Accept-Language` (Inggris/Indonesia) dan `errors` memakai path field JSON yang dikirim klien
  (`units[0].factor`), sehingga form bisa menaruh pesan di bawah field-nya.
- **Bertipe dari ujung ke ujung.** Tipe API di web di-generate dari dokumen OpenAPI
  (`npm run api:types`); angka JSON strict, sehingga uang bertipe `number`, bukan `number | string`.
- **Teks antarmuka dua bahasa tanpa library.** Kamus bertipe: tabel Indonesia wajib punya kunci
  yang persis sama dengan Inggris, sehingga terjemahan yang hilang menggagalkan build.
