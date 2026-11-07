ALTER TABLE "professors" RENAME TO "instructors"

--> statement-breakpoint

ALTER TABLE "students" RENAME COLUMN "student_number" TO "index_number"

--> statement-breakpoint

ALTER TABLE "courses" RENAME COLUMN "title" TO "course_name"

--> statement-breakpoint

ALTER TABLE "grades" RENAME COLUMN "grade_value" TO "score"
