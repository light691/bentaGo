# 0003 — BIR compliance readiness after the store-POS MVP

- Status: ACCEPTED (operator decision, this session; date 2026-08-30)
- Deciders: operator
- Sequence: lands AFTER the store-POS trajectory (BG-P1..BG-P5, BG-009) passes the pilot
- Supersedes: nothing; resolves the BIR gap flagged in the program review
- Related: docs/00 section 9 change control (V2 ideas go to a backlog note, never into MVP tasks), docs/01 section 5 (Devices, Day close), docs/decisions/0002-store-pos-pivot.md

## Context

The operator intends to register the store with the BIR and apply for accreditation so the POS can legally issue invoices. The current plan's receipt satisfies the product requirement (80mm, 42 columns, header from store profile) but says nothing about BIR document law. The program review flagged this as a gap.

Legal grounding researched this session: Section 237 NIRC requires a sales invoice for every sale of goods of PHP 100 or more (on demand below). A sari-sari store sells goods, so the primary BIR document is a **Sales Invoice (SI)**, not an Official Receipt. Non-VAT stores at or below the PHP 3M threshold print "NON-VAT – Tax Exempt under Sec. 109(1)(BB), NIRC" (or the BMBE variant). Section 238: invoices must be printed under an Authority to Print (BIR Form 1906) or issued through a registered CRM/POS machine (Forms 1900/1907, Permit To Use). POS tape receipts must carry the mandatory data, and Z-read (end-of-day) reports are kept. Content requirements come from RR 18-2012, RR 16-2018, RMC 72-2020, and the CREATE amendments.

## Decision

1. BIR compliance is a POST-PILOT workstream. It does not enter the MVP scope. A backlog note records it (docs/backlog.md).
2. Three software readiness items are cheap ONLY during the BG-P1 schema reset and MUST be decided there, because retrofitting them after real sales exist is expensive:
   - **Serial issuing model.** Sale numbers must support a BIR-accredited serial range (consecutive, 12-13 digits per RR 18-2012), issued once, never reused, gaps impossible. This resolves the receipt-numbering edge the review flagged.
   - **Terminal and machine identity on the document.** Terminal ID and machine serial belong on the printed invoice and in the audit row (single-till today, and BG-P4's till identity already plans the column).
   - **Retention and backup discipline.** Duplicates and Z-reports are BIR records kept for years (books 10 years, POS Z-reads commonly cited at 5). The backup policy must produce dated, off-machine-capable copies and the audit log must be append-only. (The ops review already flagged off-machine backup as a gap.)
3. When the BIR workstream lands, the app must be able to print a compliant SI: seller registered name, business style, address, TIN with branch code, the NON-VAT legend matching the operator's actual registration, date, serial + invoice number, description, quantity, unit price, gross amount, buyer details when the sale is PHP 1,000 or more, issuer name or signature, terminal/machine references, and the footer legend "THIS DOCUMENT IS NOT VALID FOR CLAIM OF INPUT TAX".
4. Terminology note for the next docs revision: "receipt" in docs/01 means the printed document BIR law calls a Sales Invoice for goods. A future docs pass should align the wording; this ADR records the mapping.

## What stays outside the app

Registration and filings are operator paperwork, not software: DTI/business registration, BIR Form 1901 registration and COR (2303), annual registration fee (0605), registered books of accounts, the PTU application itself, quarterly percentage tax (2551Q, 3% for non-VAT), and annual income tax (1701). The app's job is to make the technical requirements trivial: compliant documents, unbroken serials, retained Z-reports, exportable books-ready data.

## Consequences

- BG-P1's schema reset must reserve the serial-range and terminal-identity fields (column cost: near zero; retrofit cost: high).
- The BIR workstream becomes a real unit after BG-009, scoped when the operator completes registration (the NON-VAT vs BMBE legend and the accredited serial range are inputs only the operator can supply).
- The docs amendment proposed by the program review should fold items 2 into the BG-P1 box rather than defer them.
