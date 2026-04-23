# DB2 z/OS V12 & V13 — Test Modeli v1 & v2

CC test seti. Aynı DDL hem V12 hem V13'te çalışır.

**SQL dosyaları:** `db2_zos_v12_v13_v1.sql`, `db2_zos_v12_v13_v2.sql`
**ALTER referans:** `db2_zos_v12_v13_v1_to_v2.sql`

## Erwin Uyumluluk Notu

Defensive yazım kuralları:
- `CONSTRAINT <n>` syntax'ı kullanılmıyor — constraint'ler isimsiz
- PK tabloda; UNIQUE, CHECK, FK ayrı `ALTER TABLE ... ADD` ile
- **Trigger'lar yorumlu** (Erwin DB2 z/OS parser `BEGIN ATOMIC`, `OR`, `WHEN` syntax'ını parse edemiyor)
- **Generated column** (`line_total`) yorumlu (parser sorunlu)
- **INCLUDE clause** non-unique index'te kullanılmadı (Erwin "INCLUDE sadece UNIQUE'te" diyor)
- Partition syntax sade tutuldu (manuel ayar)

## Tablo Envanteri

| Tablo | v1 | v2 | Not |
|---|---|---|---|
| `APP.PRODUCT` | ✓ | ✓ | Master, baseline |
| `APP.PROMOTION` | ✓ basit | ✓ composite PK | PART-02 manuel |
| `APP.CAMPAIGN` | ❌ | ✓ | TBL-01 yeni |
| `APP.CUSTOMER` | ✓ | ✓ | Çok değişken |
| `APP.ORDERS` | ✓ | ✓ | Çok değişken |
| `SALES.ORDER_ITEM` → `OPS.ORDER_ITEM` | ✓ SALES | ✓ OPS | TBL-04 schema move |
| `APP.TRANSACTION_LOG` | ✓ basit | ✓ IDENTITY+PK+FK | Birçok case |
| `APP.PRODUCT_ARCHIVE` | ✓ | ❌ | TBL-02 silinir |
| `APP.CUSTOMER_BACKUP` → `APP.CUSTOMER_HISTORY` | ✓ | ✓ | TBL-03 rename |
| Views (3 → 3) | 3 view | 3 view | VW case'leri |

## Relation Matrisi (FK)

| FK Çift | Child | Child Kolon | Parent | Parent Kolon | v1 | v2 | Cascade | Case |
|---|---|---|---|---|---|---|---|---|
| ORDERS→CUSTOMER | ORDERS | customer_id | CUSTOMER | customer_id | ✓ | ✓ | NO ACTION | (baseline) |
| ORDERS→PROMOTION | ORDERS | promotion_id | PROMOTION | promotion_id | ✓ | ❌ | — | **FK-02** drop |
| ORDER_ITEM→ORDERS | ORDER_ITEM | order_id | ORDERS | order_id | ✓ | ✓ | NO ACTION → CASCADE | **FK-03** |
| ORDER_ITEM→PRODUCT | ORDER_ITEM | product_id | PRODUCT | product_id | ✓ | ✓ | NO ACTION | (baseline) |
| TRANSACTION_LOG→CUSTOMER | TRANSACTION_LOG | customer_id | CUSTOMER | customer_id | ❌ | ✓ | NO ACTION | **FK-01** yeni |
| CAMPAIGN→PROMOTION | CAMPAIGN | (promotion_id, promotion_start) | PROMOTION | (promotion_id, start_date) | ❌ | ✓ | CASCADE | bonus |

## Case Haritası

| Case ID | Lokasyon | Açıklama | Erwin'de |
|---|---|---|---|
| TBL-01 | `CAMPAIGN` | Yeni tablo (composite FK) | SQL |
| TBL-02 | `PRODUCT_ARCHIVE` | Drop | SQL |
| TBL-03 | `CUSTOMER_BACKUP→HISTORY` | Rename | SQL |
| TBL-04 | `ORDER_ITEM`: SALES→OPS | Schema move | SQL |
| TBL-05 | `CUSTOMER` | Comment | **manuel** |
| COL-01..05, 07..11 | Çeşitli | Kolon işlemleri | SQL |
| COL-06 | `ORDER_ITEM.unit_price` | DECIMAL(18,4→12,2) | SQL |
| COL-12 | `CUSTOMER.full_name` | CCSID EBCDIC → UNICODE | **manuel** |
| COL-13 | `TRANSACTION_LOG.log_id` | IDENTITY | SQL |
| COL-14 | `ORDER_ITEM.line_total` | Generated column | **manuel** |
| COL-15 | `ORDERS.order_amount` | Comment | **manuel** |
| PK-01, PK-02 | TX_LOG, ORDER_ITEM | PK ekleme/swap | SQL |
| UQ-01, UQ-02 | CUSTOMER | email +UNIQUE, tax_no -UNIQUE | SQL |
| CHK-01, CHK-02 | ORDER_ITEM, ORDERS | CHECK | SQL |
| FK-01, FK-02, FK-03 | (üstte matriste) | FK işlemleri | SQL |
| IDX-01, IDX-02, IDX-03, IDX-04, IDX-05, IDX-06, IDX-10 | Çeşitli | Index | SQL |
| IDX-07 | `ORDERS.IX_ORDERS_PK` | CLUSTER (zaten var) | (no change) |
| IDX-08 | (atlandı) | DB2 z/OS'ta yok | — |
| IDX-09 | `IX_CUSTOMER_EMAIL` | INCLUDE(full_name) | **manuel** |
| PART-01 | `TRANSACTION_LOG` | RANGE partition | **manuel** |
| PART-02 | `PROMOTION` | RANGE partition | **manuel** |
| STO-01 | `ORDER_ITEM` | Tablespace TS_OIS→TS_OI | SQL (IN clause var) |
| STO-02 | `TRANSACTION_LOG` (TS_TXL_PT) | COMPRESS YES (tablespace level) | SQL (CREATE TABLESPACE'da) |
| TRG-01 | `TR_CUSTOMER_AUDIT` | Yeni AFTER UPDATE | **manuel** |
| TRG-02 | `TR_ORDERS_TIMESTAMP` + `TR_ORDERS_STATUS_LOG` | BEFORE + AFTER | **manuel** |
| SEQ-01, SEQ-02 | (üstte) | Sequence | SQL |
| VW-01, VW-02, VW-03, VW-04 | Çeşitli | View işlemleri | SQL |
| VW-05 | (atlandı) | DB2 z/OS MQT restrictive | — |

**Toplam 47 case** (DB2'de IDX-08 ve VW-05 atlandı). SQL ile import: 39 case. Manuel: 8 case.

## Erwin'de Manuel Ayar Talimatları

| Case | Erwin'de Yol |
|---|---|
| TBL-05, COL-15 | Table/Column Editor → Comment |
| COL-12 | Column Editor → CCSID property |
| COL-14 | Column Editor → Generated → Formula `quantity * unit_price` |
| IDX-09 | Index Editor → INCLUDE Columns + UNIQUE flag |
| PART-01, PART-02 | Table Editor → Partitions (PARTITION BY RANGE) |
| TRG-01, TRG-02 | Trigger Editor → Body manuel |

**Önemli (TRG-02):** v2'de iki ayrı trigger var: `TR_ORDERS_TIMESTAMP` (BEFORE, sadece timestamp set) + `TR_ORDERS_STATUS_LOG` (AFTER UPDATE OF order_status, INSERT INTO TRANSACTION_LOG). DB2 z/OS'ta BEFORE trigger içinden INSERT yapılamadığı için iki trigger gerekti.
