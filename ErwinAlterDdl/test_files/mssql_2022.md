# MSSQL 2022 — Test Modeli v1 & v2

CC test seti. v1 baseline, v2 değişiklik uygulanmış hali.

**SQL dosyaları:** `mssql_2022_v1.sql` (baseline), `mssql_2022_v2.sql` (değişiklikli)
**ALTER referans:** `mssql_2022_v1_to_v2.sql`

## Erwin Uyumluluk Notu

SQL dosyaları Erwin DM 15.2 reverse engineer parser'ı ile uyumlu olacak şekilde **defensive yazım** kullanıyor:

- `CONSTRAINT <name>` syntax'ı kullanılmıyor (Erwin parse edemiyor) — constraint'ler isimsiz
- PK tabloda; UNIQUE, CHECK, FK ayrı `ALTER TABLE ... ADD` ile
- `DATETIME2(0)` yerine `DATETIME2`
- `VARCHAR(MAX)` yerine `NVARCHAR(MAX)`
- `COLLATE`, computed column, filtered index, partition, compression, indexed view → manuel

**Sonuç:** Constraint isimleri Erwin tarafından otomatik atanır (genelde `R_1`, `R_2` gibi). CC karşılaştırmasında bu fark beklenir ve göz ardı edilmelidir.

## Tablo Envanteri

| Tablo | v1 | v2 | Not |
|---|---|---|---|
| `app.PRODUCT` | ✓ | ✓ | Master, baseline (değişmez) |
| `app.PROMOTION` | ✓ basit PK | ✓ composite PK | PART-02 partition manuel |
| `app.CAMPAIGN` | ❌ | ✓ | TBL-01 yeni tablo |
| `app.CUSTOMER` | ✓ | ✓ | Çok değişken |
| `app.ORDERS` | ✓ | ✓ | Çok değişken |
| `sales.ORDER_ITEM` → `ops.ORDER_ITEM` | ✓ sales | ✓ ops | TBL-04 schema move |
| `app.TRANSACTION_LOG` | ✓ basit | ✓ IDENTITY+PK+FK | Birçok case birden |
| `app.PRODUCT_ARCHIVE` | ✓ | ❌ | TBL-02 silinir |
| `app.CUSTOMER_BACKUP` → `app.CUSTOMER_HISTORY` | ✓ | ✓ | TBL-03 rename |
| `app.VW_CUSTOMER_SUMMARY` | ✓ 3 col | ✓ 4 col | VW-03 |
| `app.VW_ORDER_DETAIL` | ✓ basit | ✓ WHERE genişledi | VW-04 (VW-05 indexed manuel) |
| `app.VW_LEGACY_CUSTOMER` | ✓ | ❌ | VW-02 silinir |
| `app.VW_TRANSACTION_DAILY` | ❌ | ✓ | VW-01 yeni view |

## Relation Matrisi (FK)

| FK Çift | Child | Child Kolon | Parent | Parent Kolon | v1 | v2 | Cascade v1 | Cascade v2 | Case |
|---|---|---|---|---|---|---|---|---|---|
| ORDERS→CUSTOMER | ORDERS | customer_id | CUSTOMER | customer_id | ✓ | ✓ | NO ACTION | NO ACTION | (baseline) |
| ORDERS→PROMOTION | ORDERS | promotion_id | PROMOTION | promotion_id | ✓ | ❌ | NO ACTION | — | **FK-02** drop |
| ORDER_ITEM→ORDERS | ORDER_ITEM | order_id | ORDERS | order_id | ✓ | ✓ | NO ACTION | CASCADE | **FK-03** cascade |
| ORDER_ITEM→PRODUCT | ORDER_ITEM | product_id | PRODUCT | product_id | ✓ | ✓ | NO ACTION | NO ACTION | (baseline) |
| TRANSACTION_LOG→CUSTOMER | TRANSACTION_LOG | customer_id | CUSTOMER | customer_id | ❌ | ✓ | — | NO ACTION | **FK-01** yeni |
| CAMPAIGN→PROMOTION | CAMPAIGN | (promotion_id, promotion_start_date) | PROMOTION | (promotion_id, start_date) | ❌ | ✓ | — | CASCADE | bonus (TBL-01) |

## Case Haritası

| Case ID | Case | Lokasyon | v1 → v2 | Erwin'de |
|---|---|---|---|---|
| TBL-01 | Yeni tablo | `app.CAMPAIGN` | yok → CREATE | SQL ile |
| TBL-02 | Tablo silme | `app.PRODUCT_ARCHIVE` | var → yok | SQL ile |
| TBL-03 | Tablo rename | `CUSTOMER_BACKUP` → `CUSTOMER_HISTORY` | rename | SQL ile |
| TBL-04 | Schema değişimi | `ORDER_ITEM`: sales → ops | schema move | SQL ile |
| TBL-05 | Tablo comment | `app.CUSTOMER` | yok → comment | **manuel** |
| COL-01 | Yeni kolon | `app.CUSTOMER.email_verified` | yok → BIT | SQL ile |
| COL-02 | Kolon silme | `app.CUSTOMER.fax_number` | var → yok | SQL ile |
| COL-03 | Kolon rename | `mobile_phone` → `mobile_no` | rename | SQL ile |
| COL-04 | Type değişimi | `app.ORDERS.order_amount` | INT → BIGINT | SQL ile |
| COL-05 | Length artırma | `app.CUSTOMER.address` | VARCHAR(100→250) | SQL ile |
| COL-06 | Precision azaltma | `ops.ORDER_ITEM.unit_price` | DECIMAL(18,4→12,2) | SQL ile |
| COL-07 | NULL → NOT NULL | `app.ORDERS.customer_id` | nullable → NOT NULL | SQL ile |
| COL-08 | NOT NULL → NULL | `ops.ORDER_ITEM.discount_pct` | NOT NULL → nullable | SQL ile |
| COL-09 | DEFAULT ekleme | `app.CUSTOMER.status` | yok → 'ACTIVE' | SQL ile |
| COL-10 | DEFAULT değiştirme | `app.ORDERS.order_status` | 'NEW' → 'PENDING' | SQL ile |
| COL-11 | DEFAULT silme | `ops.ORDER_ITEM.quantity` | DEFAULT 1 → yok | SQL ile |
| COL-12 | Collation değişimi | `app.CUSTOMER.full_name` | SQL_Latin1 → Turkish_CI_AS | **manuel** |
| COL-13 | Identity ekleme | `app.TRANSACTION_LOG.log_id` | yok → IDENTITY | SQL ile |
| COL-14 | Computed column | `ops.ORDER_ITEM.line_total` | yok → AS PERSISTED | **manuel** |
| COL-15 | Comment değişimi | `app.ORDERS.order_amount` | yok → MS_Description | **manuel** |
| PK-01 | PK ekleme | `app.TRANSACTION_LOG` | yok → PK | SQL ile |
| PK-02 | PK swap | `ops.ORDER_ITEM` | PK(order_id) → PK(order_id, line_no) | SQL ile |
| UQ-01 | UNIQUE ekleme | `app.CUSTOMER.email` | yok → UNIQUE | SQL ile |
| UQ-02 | UNIQUE silme | `app.CUSTOMER.tax_no` | UNIQUE → yok | SQL ile |
| CHK-01 | CHECK ekleme | `ops.ORDER_ITEM` | yok → quantity > 0 | SQL ile |
| CHK-02 | CHECK değiştirme | `app.ORDERS` | >0 → range | SQL ile |
| FK-01 | FK ekleme | TX_LOG → CUSTOMER | yok → FK | SQL ile |
| FK-02 | FK silme | ORDERS → PROMOTION | var → yok | SQL ile |
| FK-03 | FK cascade değişimi | ORDER_ITEM → ORDERS | NO ACTION → CASCADE | SQL ile |
| IDX-01 | Yeni index | `IX_CUSTOMER_EMAIL` | yok → var | SQL ile |
| IDX-02 | Index silme | `IX_ORDERS_DATE` | var → yok | SQL ile |
| IDX-03 | Index rename | `IX_OI_PROD` → `IX_ORDER_ITEM_PRODUCT` | rename | SQL ile |
| IDX-04 | Index kolon ekleme | `IX_ORDERS_CUSTOMER` | (cust) → (cust, date) | SQL ile |
| IDX-05 | Index kolon sırası | `IX_TX_LOG` | (date,cust) → (cust,date) | SQL ile |
| IDX-06 | UNIQUE flag | `IX_CUSTOMER_TAXNO` | non-unique → UNIQUE | SQL ile |
| IDX-07 | Clustered swap | `app.ORDERS` PK | NCL → CL | **manuel** |
| IDX-08 | Filtered index | `IX_CUSTOMER_EMAIL` v2 | yok → WHERE status='ACTIVE' | **manuel** |
| IDX-09 | INCLUDE | `IX_CUSTOMER_EMAIL` v2 | yok → INCLUDE(full_name) | SQL ile |
| IDX-10 | DESC kolon | `IX_ORDERS_DATE_DESC` | yeni DESC | SQL ile |
| PART-01 | Partition ekleme | `app.TRANSACTION_LOG` | yok → RANGE | **manuel** |
| PART-02 | Partitioned table | `app.PROMOTION` | basit → partitioned | **manuel** |
| STO-01 | Filegroup değişimi | `ops.ORDER_ITEM` | PRIMARY → FG_DATA | **manuel** |
| STO-02 | Compression | `app.TRANSACTION_LOG` | yok → PAGE | **manuel** |
| TRG-01 | Trigger ekleme | `TR_CUSTOMER_AUDIT` | yok → AFTER UPDATE | SQL ile |
| TRG-02 | Trigger body | `TR_ORDERS_TIMESTAMP` | basit → genişlemiş | SQL ile |
| SEQ-01 | Sequence ekleme | `SEQ_TRANSACTION_LOG` | yok → CREATE | SQL ile |
| SEQ-02 | Sequence değişimi | `SEQ_ORDER_NUMBER` | INCREMENT 1 → 10 | SQL ile |
| VW-01 | Yeni view | `VW_TRANSACTION_DAILY` | yok → CREATE | SQL ile |
| VW-02 | View silme | `VW_LEGACY_CUSTOMER` | var → yok | SQL ile |
| VW-03 | View body kolon | `VW_CUSTOMER_SUMMARY` | 3 → 4 col | SQL ile |
| VW-04 | View body WHERE | `VW_ORDER_DETAIL` | =OPEN → IN | SQL ile |
| VW-05 | Indexed view | `VW_ORDER_DETAIL` v2 | yok → SCHEMABINDING+indexed | **manuel** |

**Toplam 50 case.** SQL ile import: 41 case. Erwin'de manuel ayar: 9 case (TBL-05, COL-12, COL-14, COL-15, IDX-07, IDX-08, PART-01, PART-02, STO-01, STO-02, VW-05).

## Erwin'de Manuel Ayar Talimatları

| Case | Erwin'de Yol |
|---|---|
| TBL-05, COL-15 | Table/Column Editor → Comment alanı |
| COL-12 | Column Editor → Properties → Collation |
| COL-14 | Column Editor → Computed Column → Formula `quantity * unit_price`, PERSISTED |
| IDX-07 | Index Editor (PK_ORDERS) → Index Type: Non-Clustered |
| IDX-08 | Index Editor (IX_CUSTOMER_EMAIL/TAXNO) → Where Clause |
| PART-01, PART-02 | Table Editor → Partitions tab → Partition Function/Scheme tanımla |
| STO-01 | Table Editor → Storage → Filegroup |
| STO-02 | Table Editor → Storage → Data Compression: PAGE |
| VW-05 | View Editor → Properties → "Schema Binding" + Materialized |