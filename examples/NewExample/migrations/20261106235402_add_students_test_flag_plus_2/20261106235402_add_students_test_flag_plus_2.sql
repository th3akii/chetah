ALTER TABLE "students" ADD COLUMN "test_flag" boolean

--> statement-breakpoint

ALTER TABLE "students" ADD COLUMN "invalid_reference" integer

--> statement-breakpoint

ALTER TABLE "students" ADD CONSTRAINT "students_invalid_reference_fkey" FOREIGN KEY ("invalid_reference") REFERENCES "departments" ("id")
